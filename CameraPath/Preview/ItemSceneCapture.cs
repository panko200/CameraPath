using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media.Imaging;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Player.Video;

namespace CameraPath.Preview
{
    /// <summary>
    /// どのアイテムの何番目の出力か。立ち絵のように1つのアイテムが複数の出力を持つことがある。
    /// </summary>
    internal readonly record struct CaptureKey(object Item, int OutputIndex);

    /// <summary>アイテムはIDで比較する実装があり得るので、参照で比較する。</summary>
    internal sealed class CaptureKeyComparer : IEqualityComparer<CaptureKey>
    {
        public bool Equals(CaptureKey x, CaptureKey y)
            => ReferenceEquals(x.Item, y.Item) && x.OutputIndex == y.OutputIndex;

        public int GetHashCode(CaptureKey key)
            => (RuntimeHelpers.GetHashCode(key.Item) * 397) ^ key.OutputIndex;
    }

    /// <summary>
    /// 3Dプレビューに描くアイテム1つ分。
    /// 四隅はカメラの変換を含まないワールド座標なので、カメラを回してもアイテムは動かない。
    /// </summary>
    internal sealed record CapturedSceneItem(
        Vector3 A,
        Vector3 B,
        Vector3 C,
        Vector3 D,
        string Label,
        int Layer,
        bool Affected,
        BitmapSource? Texture,
        long Frame)
    {
        public bool IsCloseTo(CapturedSceneItem other, float tolerance = 0.05f)
            => Frame == other.Frame
            && Label == other.Label
            && Layer == other.Layer
            && Affected == other.Affected
            && ReferenceEquals(Texture, other.Texture)
            && IsClose(A, other.A, tolerance)
            && IsClose(B, other.B, tolerance)
            && IsClose(C, other.C, tolerance)
            && IsClose(D, other.D, tolerance);

        private static bool IsClose(Vector3 a, Vector3 b, float tolerance)
            => Vector3.DistanceSquared(a, b) <= tolerance * tolerance;
    }

    /// <summary>あるフレームのアイテムの集まり。</summary>
    internal sealed record CapturedScene(IReadOnlyList<CapturedSceneItem> Items, long Frame);

    /// <summary>
    /// YMM4の描画から控えたアイテム情報の置き場。
    /// 描画スレッドが書き込み、UIスレッドが読む。
    /// </summary>
    internal static class ItemSceneCapture
    {
        internal const float DegToRad = MathF.PI / 180f;

        /// <summary>フレームが分からないときに、古い情報を消さずにおいておくための印。</summary>
        internal const long UnknownFrame = -1;

        private static readonly object gate = new();
        private static readonly Dictionary<CaptureKey, CapturedSceneItem> entries = new(new CaptureKeyComparer());
        private static CapturedScene? latest;
        private static int version;
        private static int subscribers;
        private static bool installAttempted;

        /// <summary>内容が変わるたびに増える。プレビューはこれを見て描き直す。</summary>
        public static int Version => Volatile.Read(ref version);

        /// <summary>プレビューが表示されているか。表示されていないときは何も取らない。</summary>
        internal static bool IsActive => Volatile.Read(ref subscribers) > 0;

        /// <summary>プレビューが表示されたときに呼ぶ。最初の1つでパッチを当てる。</summary>
        public static void Subscribe()
        {
            if (Interlocked.Increment(ref subscribers) != 1)
                return;

            if (installAttempted)
                return;

            installAttempted = true;
            TimelineScenePatch.TryInstall();
        }

        /// <summary>プレビューが閉じられたときに呼ぶ。</summary>
        public static void Unsubscribe()
        {
            if (Interlocked.Decrement(ref subscribers) > 0)
                return;

            lock (gate)
            {
                entries.Clear();
                latest = null;
            }

            ItemTextureCache.Clear();
            Interlocked.Increment(ref version);
        }

        /// <summary>最新の内容。</summary>
        public static CapturedScene? Latest
        {
            get
            {
                lock (gate)
                    return latest;
            }
        }

        /// <summary>描画スレッドから、1回の描画で取れたアイテムをまとめて渡す。</summary>
        internal static void Update(long frame, List<KeyValuePair<CaptureKey, CapturedSceneItem>> captured)
        {
            List<CaptureKey>? removed = null;
            var changed = false;

            lock (gate)
            {
                if (frame != UnknownFrame)
                {
                    // 同じフレームで取れなかったアイテムは、もう描かれていない
                    foreach (var key in entries.Keys)
                    {
                        if (entries[key].Frame != frame)
                            (removed ??= []).Add(key);
                    }

                    if (removed is not null)
                    {
                        foreach (var key in removed)
                            entries.Remove(key);
                    }
                }

                foreach (var pair in captured)
                    entries[pair.Key] = pair.Value;

                var items = new List<CapturedSceneItem>(entries.Count);
                foreach (var entry in entries.Values)
                    items.Add(entry);

                items.Sort(static (x, y) => x.Layer != y.Layer
                    ? x.Layer.CompareTo(y.Layer)
                    : string.CompareOrdinal(x.Label, y.Label));

                if (!IsSameAs(latest, items, frame))
                {
                    latest = new CapturedScene(items, frame);
                    changed = true;
                }
            }

            if (removed is not null)
            {
                foreach (var key in removed)
                    ItemTextureCache.Remove(key);
            }

            if (changed)
                Interlocked.Increment(ref version);
        }

        private static bool IsSameAs(CapturedScene? previous, List<CapturedSceneItem> items, long frame)
        {
            if (previous is null || previous.Frame != frame || previous.Items.Count != items.Count)
                return false;

            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].IsCloseTo(previous.Items[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 画像の範囲と配置から、カメラを適用する前の四隅（ワールド座標）を求める。
        /// 変換の順序は YMM4 の EffectedItemSource.TransformBoundsQuad と同じで、
        /// カメラ行列だけを掛けない（プレビューではアイテムを世界に固定して見たいため）。
        /// </summary>
        internal static bool TryGetWorldQuad(
            RawRectF bounds,
            DrawDescription description,
            out Vector3 a,
            out Vector3 b,
            out Vector3 c,
            out Vector3 d)
        {
            a = b = c = d = default;

            var invert = description.Invert
                ? Matrix3x2.CreateScale(-1f, 1f, description.CenterPoint)
                : Matrix3x2.Identity;

            var local = invert * Matrix3x2.CreateScale(description.Zoom);

            var world = Matrix4x4.CreateRotationZ(description.Rotation.Z * DegToRad)
                * Matrix4x4.CreateRotationY(-description.Rotation.Y * DegToRad)
                * Matrix4x4.CreateRotationX(-description.Rotation.X * DegToRad)
                * Matrix4x4.CreateTranslation(description.Draw);

            return TryTransform(new Vector2(bounds.Left, bounds.Top), local, world, out a)
                && TryTransform(new Vector2(bounds.Right, bounds.Top), local, world, out b)
                && TryTransform(new Vector2(bounds.Right, bounds.Bottom), local, world, out c)
                && TryTransform(new Vector2(bounds.Left, bounds.Bottom), local, world, out d);
        }

        private static bool TryTransform(Vector2 corner, Matrix3x2 local, Matrix4x4 world, out Vector3 result)
        {
            var point = Vector2.Transform(corner, local);
            var transformed = Vector4.Transform(new Vector4(point.X, point.Y, 0f, 1f), world);

            result = new Vector3(transformed.X, transformed.Y, transformed.Z);
            return float.IsFinite(result.X) && float.IsFinite(result.Y) && float.IsFinite(result.Z);
        }
    }
}
