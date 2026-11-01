using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace CameraPath.Preview
{
    /// <summary>
    /// YMM4の描画（TimelineSource.Update）にPostfixを当てて、
    /// その時点で描かれているアイテムの位置と絵を3Dプレビュー用に控える。
    ///
    /// エフェクトのプロセッサ経由だと「このエフェクトが適用されたアイテム1つ」しか分からないが、
    /// ここではタイムラインが持っている全アイテムを見るので、グループ制御の中のアイテムも出る。
    /// Easy3DPreview と同じ考え方で、YMM4のアップデートで内部構造が変わると動かなくなる。
    ///
    /// グループ制御の中のアイテムは、外側のTimelineSourceが描画している最中に
    /// 内側のTimelineSourceが更新される。描画中にデバイスを触ると壊れるので、
    /// アイテムの収集だけをその場で行い、絵の読み戻しは一番外側の更新が終わってから行う。
    /// </summary>
    internal static class TimelineScenePatch
    {
        private const string HarmonyId = "CameraPath.ItemSceneCapture";

        /// <summary>1回の描画で作り直す絵の数の上限。多いときは前回の絵を使い回す。</summary>
        private const int MaxTexturesPerPass = 12;

        /// <summary>取り込みすぎて重くならないようにする上限。</summary>
        private const int MaxPendingItems = 256;

        private static FieldInfo? resourcesField;
        private static FieldInfo? devicesField;
        private static FieldInfo? sceneField;
        private static FieldInfo? outputsField;
        private static PropertyInfo? sourceProperty;
        private static PropertyInfo? parentEffectsProperty;
        private static PropertyInfo? drawDescriptionProperty;
        private static PropertyInfo? preRenderOutputProperty;
        private static PropertyInfo? sceneTimelineProperty;
        private static PropertyInfo? timelineVideoInfoProperty;
        private static PropertyInfo? videoInfoFpsProperty;
        private static Type? compositeSourceType;

        private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> propertyCache = new();

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static List<PendingItem>? pending;

        private static int reportCount;

        internal static bool TryInstall()
        {
            try
            {
                var timelineSourceType = AccessTools.TypeByName("YukkuriMovieMaker.Player.Video.TimelineSource");
                var effectedItemSourceType = AccessTools.TypeByName("YukkuriMovieMaker.Player.Video.EffectedItemSource");
                var effectedSourceOutputType = AccessTools.TypeByName("YukkuriMovieMaker.Player.Video.EffectedSourceOutput");

                if (timelineSourceType is null || effectedItemSourceType is null || effectedSourceOutputType is null)
                {
                    Report("YMM4の内部型が見つかりません。アイテムはプレビューに描かれません。");
                    return false;
                }

                resourcesField = AccessTools.Field(timelineSourceType, "timelineResources");
                devicesField = AccessTools.Field(timelineSourceType, "devices");
                sceneField = AccessTools.Field(timelineSourceType, "scene");

                outputsField = AccessTools.Field(effectedItemSourceType, "effectedSourceOutputs");
                sourceProperty = AccessTools.Property(effectedItemSourceType, "Source");
                parentEffectsProperty = AccessTools.Property(effectedItemSourceType, "ParentEffects");

                drawDescriptionProperty = AccessTools.Property(effectedSourceOutputType, "DrawDescription");
                preRenderOutputProperty = AccessTools.Property(effectedSourceOutputType, "PreRenderOutput");

                compositeSourceType = AccessTools.TypeByName("YukkuriMovieMaker.Player.Video.Items.CompositeSource");

                if (resourcesField is null || devicesField is null || outputsField is null
                    || sourceProperty is null || parentEffectsProperty is null
                    || drawDescriptionProperty is null || preRenderOutputProperty is null)
                {
                    Report("YMM4の内部メンバが見つかりません。アイテムはプレビューに描かれません。");
                    return false;
                }

                var updateMethod = AccessTools.Method(
                    timelineSourceType, "Update", [typeof(TimeSpan), typeof(TimelineSourceUsage)]);

                if (updateMethod is null)
                {
                    Report("TimelineSource.Update が見つかりません。アイテムはプレビューに描かれません。");
                    return false;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    updateMethod,
                    prefix: new HarmonyMethod(typeof(TimelineScenePatch), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(TimelineScenePatch), nameof(Postfix)),
                    finalizer: new HarmonyMethod(typeof(TimelineScenePatch), nameof(Finalizer)));

                return true;
            }
            catch (Exception ex)
            {
                Report($"Harmonyパッチを当てられませんでした: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>更新の入れ子の深さを数える。1なら一番外側。</summary>
        private static void Prefix(out int __state)
        {
            __state = ++depth;

            if (__state == 1)
                pending = null;
        }

        private static void Finalizer() => depth--;

        /// <summary>
        /// TimelineSource.Update の後始末。YMM4の描画スレッドから呼ばれる。
        /// </summary>
        private static void Postfix(object __instance, TimeSpan time, int __state)
        {
            if (!ItemSceneCapture.IsActive)
            {
                if (__state == 1)
                    pending = null;

                return;
            }

            try
            {
                Collect(__instance, time);

                if (__state == 1)
                    Publish();
            }
            catch (Exception ex)
            {
                Report($"アイテム情報の取得に失敗しました: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>描画中のデバイスは触らず、アイテムの情報だけを集める。</summary>
        private static void Collect(object instance, TimeSpan time)
        {
            if (resourcesField is null || devicesField is null || outputsField is null)
                return;

            if (resourcesField.GetValue(instance) is not IDictionary resources)
                return;

            if (devicesField.GetValue(instance) is not IGraphicsDevicesAndContext devices)
                return;

            var frame = ResolveFrame(instance, time);

            foreach (DictionaryEntry entry in resources)
            {
                var item = entry.Key;
                var source = entry.Value;

                if (item is null || source is null)
                    continue;

                // グループ制御の合成結果は、中に入っているアイテムを個別に描くので飛ばす
                if (IsCompositeSource(source))
                    continue;

                if (outputsField.GetValue(source) is not IList outputs)
                    continue;

                var affected = IsAffectedByThisEffect(item, source);
                var label = GetLabel(item);
                var layer = GetIntProperty(item, "Layer");

                for (var index = 0; index < outputs.Count; index++)
                {
                    var output = outputs[index];
                    if (output is null)
                        continue;

                    if (drawDescriptionProperty?.GetValue(output) is not DrawDescription description)
                        continue;

                    if (preRenderOutputProperty?.GetValue(output) is not ID2D1Image image)
                        continue;

                    if (description.Opacity <= 0.0 || description.Zoom.X == 0f || description.Zoom.Y == 0f)
                        continue;

                    (pending ??= []).Add(new PendingItem(
                        new CaptureKey(item, index), image, description, label, layer, affected, frame, devices));

                    if (pending.Count >= MaxPendingItems)
                        return;
                }
            }
        }

        /// <summary>一番外側の更新が終わったあとで、絵を作ってプレビューへ渡す。</summary>
        private static void Publish()
        {
            var items = pending;
            pending = null;

            if (items is null || items.Count == 0)
                return;

            // 絵をまだ作っていないアイテムを先に、次にこのエフェクトが効いているアイテムを優先する
            items.Sort(static (x, y) =>
            {
                var missing = ItemTextureCache.Has(x.Key).CompareTo(ItemTextureCache.Has(y.Key));
                return missing != 0 ? missing : y.Affected.CompareTo(x.Affected);
            });

            var frame = items[0].Frame;
            var budget = MaxTexturesPerPass;
            var captured = new List<KeyValuePair<CaptureKey, CapturedSceneItem>>(items.Count);

            foreach (var item in items)
            {
                RawRectF bounds;
                try
                {
                    bounds = item.Devices.DeviceContext.GetImageLocalBounds(item.Image);
                }
                catch
                {
                    continue;
                }

                if (!ItemSceneCapture.TryGetWorldQuad(bounds, item.Description, out var a, out var b, out var c, out var d))
                    continue;

                var texture = ItemTextureCache.Get(
                    item.Key, item.Image, bounds, item.Devices, budget > 0, out var built);

                if (built)
                    budget--;

                captured.Add(new KeyValuePair<CaptureKey, CapturedSceneItem>(
                    item.Key,
                    new CapturedSceneItem(a, b, c, d, item.Label, item.Layer, item.Affected, texture, item.Frame)));
            }

            ItemSceneCapture.Update(frame, captured);
        }

        private readonly record struct PendingItem(
            CaptureKey Key,
            ID2D1Image Image,
            DrawDescription Description,
            string Label,
            int Layer,
            bool Affected,
            long Frame,
            IGraphicsDevicesAndContext Devices);

        private static bool IsCompositeSource(object source)
        {
            var value = sourceProperty?.GetValue(source);
            if (value is null)
                return false;

            return compositeSourceType is not null
                ? compositeSourceType.IsInstanceOfType(value)
                : value.GetType().Name == "CompositeSource";
        }

        /// <summary>
        /// このエフェクトが効いているアイテムか。
        /// グループ制御に付けた場合は、中のアイテムの親エフェクトとして入っている。
        /// </summary>
        private static bool IsAffectedByThisEffect(object item, object source)
        {
            if (parentEffectsProperty?.GetValue(source) is IEnumerable parents)
            {
                foreach (var pair in parents)
                {
                    if (pair is null)
                        continue;

                    if (FindProperty(pair.GetType(), "EffectItem")?.GetValue(pair) is CameraPathEffect)
                        return true;
                }
            }

            if (FindProperty(item.GetType(), "VideoEffects")?.GetValue(item) is IEnumerable effects)
            {
                foreach (var effect in effects)
                {
                    if (effect is CameraPathEffect)
                        return true;
                }
            }

            return false;
        }

        private static string GetLabel(object item)
        {
            var label = FindProperty(item.GetType(), "Label")?.GetValue(item) as string;
            return string.IsNullOrWhiteSpace(label) ? item.GetType().Name : label;
        }

        private static int GetIntProperty(object target, string name)
        {
            try
            {
                var value = FindProperty(target.GetType(), name)?.GetValue(target);
                return value is null ? 0 : Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// いま描いているフレーム。分からないときは古い情報を消さない印を返す。
        /// </summary>
        private static long ResolveFrame(object instance, TimeSpan time)
        {
            var scene = sceneField?.GetValue(instance);
            if (scene is null)
                return ItemSceneCapture.UnknownFrame;

            var fps = GetFps(scene);
            if (fps <= 0.0)
                return ItemSceneCapture.UnknownFrame;

            var frame = (long)Math.Round(time.TotalSeconds * fps);
            return frame < 0 ? ItemSceneCapture.UnknownFrame : frame;
        }

        private static double GetFps(object scene)
        {
            try
            {
                var timeline = (sceneTimelineProperty ??= FindProperty(scene.GetType(), "Timeline"))?.GetValue(scene);
                if (timeline is null)
                    return 0.0;

                var videoInfo = (timelineVideoInfoProperty ??= FindProperty(timeline.GetType(), "VideoInfo"))?.GetValue(timeline);
                if (videoInfo is null)
                    return 0.0;

                var fps = (videoInfoFpsProperty ??= FindProperty(videoInfo.GetType(), "FPS"))?.GetValue(videoInfo);
                return fps is null ? 0.0 : Convert.ToDouble(fps);
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>型名から、引数のないプロパティを探す。探した結果は型ごとに覚えておく。</summary>
        private static PropertyInfo? FindProperty(Type type, string name)
            => propertyCache.GetOrAdd((type, name), static key => Find(key.Type, key.Name));

        private static PropertyInfo? Find(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            for (var current = type; current is not null; current = current.BaseType)
            {
                var property = current.GetProperty(name, flags, null, null, Type.EmptyTypes, null);
                if (property is not null)
                    return property;
            }

            foreach (var contract in type.GetInterfaces())
            {
                var property = contract.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property is not null)
                    return property;
            }

            return null;
        }

        private static void Report(string message)
        {
            if (Interlocked.Increment(ref reportCount) > 8)
                return;

            Debug.WriteLine($"[CameraPath] {message}");
        }
    }
}
