using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace CameraPath.Preview
{
    /// <summary>
    /// アイテムの絵を、WPFで四角形に貼れるビットマップに作り変える。
    /// プレビューは小さいので、長辺を上限まで縮めてから読み戻す。
    /// </summary>
    internal static class ItemTextureCache
    {
        /// <summary>作るビットマップの長辺の上限（px）。</summary>
        private const int MaxTextureSize = 512;

        /// <summary>同じアイテムの絵を作り直す最短間隔（ミリ秒）。</summary>
        private const long RefreshInterval = 150;

        /// <summary>作り続けに失敗するアイテムは諦める回数。</summary>
        private const int MaxFailures = 3;

        private sealed class Entry
        {
            public BitmapSource? Texture;
            public long Tick;
            public int Failures;
        }

        private static readonly Dictionary<CaptureKey, Entry> entries = new(new CaptureKeyComparer());
        private static byte[] pixels = [];

        public static void Clear()
        {
            lock (entries)
            {
                entries.Clear();
                pixels = [];
            }
        }

        public static void Remove(CaptureKey key)
        {
            lock (entries)
                entries.Remove(key);
        }

        /// <summary>このアイテムの絵をまだ作っていないか。</summary>
        public static bool Has(CaptureKey key)
        {
            lock (entries)
                return entries.TryGetValue(key, out var entry) && entry.Texture is not null;
        }

        /// <summary>
        /// 絵を取り出す。作り直す間隔の中なら前回のものを返す。
        /// <paramref name="allowBuild"/> が false のときは作り直さず、前回のものだけを返す。
        /// </summary>
        public static BitmapSource? Get(
            CaptureKey key,
            ID2D1Image image,
            RawRectF bounds,
            IGraphicsDevicesAndContext devices,
            bool allowBuild,
            out bool built)
        {
            built = false;

            lock (entries)
            {
                if (!entries.TryGetValue(key, out var entry))
                {
                    if (!allowBuild)
                        return null;

                    entry = new Entry();
                    entries[key] = entry;
                }

                var now = Environment.TickCount64;
                if (entry.Texture is not null && (!allowBuild || now - entry.Tick < RefreshInterval))
                    return entry.Texture;

                if (!allowBuild || entry.Failures >= MaxFailures)
                    return entry.Texture;

                var texture = Create(image, bounds, devices, MaxTextureSize, ref pixels);

                if (texture is not null)
                    entry.Texture = texture;
                else
                    entry.Failures++;

                entry.Tick = now;
                built = true;
                return entry.Texture;
            }
        }

        /// <summary>Direct2Dの画像を、縮小してWPFのビットマップにする。</summary>
        internal static BitmapSource? Create(
            ID2D1Image image,
            RawRectF bounds,
            IGraphicsDevicesAndContext devices,
            int maxSize,
            ref byte[] buffer)
        {
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;

            if (!float.IsFinite(width) || !float.IsFinite(height) || width < 0.01f || height < 0.01f)
                return null;

            var scale = Math.Min(1f, maxSize / Math.Max(width, height));
            var textureWidth = Math.Max(1, (int)MathF.Round(width * scale));
            var textureHeight = Math.Max(1, (int)MathF.Round(height * scale));

            if (textureWidth > 8192 || textureHeight > 8192)
                return null;

            var deviceContext = devices.DeviceContext;
            var previousTarget = deviceContext.Target;
            var previousTransform = deviceContext.Transform;

            ID2D1Bitmap1? target = null;
            ID2D1Bitmap1? staging = null;

            try
            {
                target = deviceContext.CreateNotInitializedBitmap(
                    textureWidth, textureHeight, BitmapOptions.Target | BitmapOptions.CannotDraw);
                staging = deviceContext.CreateNotInitializedBitmap(
                    textureWidth, textureHeight, BitmapOptions.CannotDraw | BitmapOptions.CpuRead);

                deviceContext.Target = target;
                deviceContext.BeginDraw();
                deviceContext.Clear(new Color4(0f, 0f, 0f, 0f));
                deviceContext.Transform = Matrix3x2.CreateScale(scale);
                deviceContext.DrawImage(
                    image,
                    new Vector2(-bounds.Left, -bounds.Top),
                    null,
                    InterpolationMode.Linear,
                    CompositeMode.SourceOver);
                deviceContext.Transform = Matrix3x2.Identity;
                deviceContext.EndDraw();

                deviceContext.Target = previousTarget;

                staging.CopyFromBitmap(target);
                staging.ToBytes(ref buffer);

                var bitmap = BitmapSource.Create(
                    textureWidth,
                    textureHeight,
                    96,
                    96,
                    PixelFormats.Pbgra32,
                    null,
                    buffer,
                    textureWidth * 4);

                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
            finally
            {
                deviceContext.Target = previousTarget;
                deviceContext.Transform = previousTransform;
                target?.Dispose();
                staging?.Dispose();
            }
        }
    }
}
