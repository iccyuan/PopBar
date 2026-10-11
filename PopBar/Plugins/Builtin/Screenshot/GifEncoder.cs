using System.IO;

namespace PopBar.Plugins.Builtin.Screenshot;

/// <summary>
/// Streaming animated-GIF writer tuned for screen recordings. Each frame encodes only the rectangle that changed
/// since the previous one, unchanged pixels inside it become transparent, and every frame gets its own palette
/// (exact when it has ≤ 255 colours, which UI content usually does; popularity-quantized otherwise).
/// A frame is written once the next one arrives, so its delay is the real time between them.
/// </summary>
internal sealed class GifEncoder : IDisposable
{
    private readonly Stream _out;
    private readonly int _w, _h;
    private byte[]? _canvas;              // last frame as shown (raw BGRA)
    private (byte[] Block, long Ms)? _pending; // encoded image block waiting for its delay
    private bool _finished;

    public int FramesWritten { get; private set; }

    public GifEncoder(Stream output, int width, int height)
    {
        _out = output; _w = width; _h = height;
        Write("GIF89a"u8);
        U16(width); U16(height);
        _out.WriteByte(0x70); // no global colour table, 8-bit colour resolution
        _out.WriteByte(0); _out.WriteByte(0);
        // NETSCAPE2.0: loop forever
        _out.Write([0x21, 0xFF, 0x0B]); Write("NETSCAPE2.0"u8); _out.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    /// <summary>Add a top-down BGRA frame (stride = width*4) captured at <paramref name="ms"/>.</summary>
    public void AddFrame(byte[] bgra, long ms)
    {
        var rect = _canvas == null ? (0, 0, _w, _h) : Diff(_canvas, bgra);
        if (rect.Item3 == 0) return; // identical: the pending frame simply lasts longer
        var block = EncodeBlock(bgra, rect, _canvas);
        Flush(ms);
        _pending = (block, ms);
        _canvas ??= new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, _canvas, 0, bgra.Length);
    }

    public void Finish(long endMs)
    {
        if (_finished) return;
        _finished = true;
        Flush(Math.Max(endMs, (_pending?.Ms ?? 0) + 100));
        _out.WriteByte(0x3B);
        _out.Flush();
    }

    private void Flush(long nowMs)
    {
        if (_pending is not { } p) return;
        int cs = (int)Math.Clamp((nowMs - p.Ms + 5) / 10, 2, 65535);
        // Patch the delay into the graphic control extension at the start of the block.
        p.Block[4] = (byte)cs; p.Block[5] = (byte)(cs >> 8);
        _out.Write(p.Block);
        FramesWritten++;
        _pending = null;
    }

    /// <summary>Bounding box of pixels that differ (x, y, w, h); w = 0 when identical.</summary>
    private (int, int, int, int) Diff(byte[] a, byte[] b)
    {
        int stride = _w * 4, minX = _w, minY = _h, maxX = -1, maxY = -1;
        for (int y = 0; y < _h; y++)
        {
            var ra = a.AsSpan(y * stride, stride);
            var rb = b.AsSpan(y * stride, stride);
            if (ra.SequenceEqual(rb)) continue;
            int l = 0; while (ra[l] == rb[l] && ra[l + 1] == rb[l + 1] && ra[l + 2] == rb[l + 2]) l += 4;
            int r = stride - 4; while (ra[r] == rb[r] && ra[r + 1] == rb[r + 1] && ra[r + 2] == rb[r + 2]) r -= 4;
            minX = Math.Min(minX, l / 4); maxX = Math.Max(maxX, r / 4);
            if (minY == _h) minY = y;
            maxY = y;
        }
        return maxX < 0 ? (0, 0, 0, 0) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private byte[] EncodeBlock(byte[] px, (int X, int Y, int W, int H) r, byte[]? prev)
    {
        int stride = _w * 4, n = r.W * r.H;
        var colors = new int[n];
        bool anyTransparent = false;
        for (int y = 0; y < r.H; y++)
        {
            int row = (r.Y + y) * stride + r.X * 4;
            for (int x = 0; x < r.W; x++)
            {
                int i = row + x * 4;
                if (prev != null && prev[i] == px[i] && prev[i + 1] == px[i + 1] && prev[i + 2] == px[i + 2]) { colors[y * r.W + x] = -1; anyTransparent = true; }
                else colors[y * r.W + x] = px[i + 2] << 16 | px[i + 1] << 8 | px[i];
            }
        }

        // Palette: index 0 is reserved for "unchanged" when the frame has any.
        int first = anyTransparent ? 1 : 0, room = 256 - first;
        var palette = new List<int>();
        if (anyTransparent) palette.Add(0);
        var indices = new byte[n];
        var exact = new Dictionary<int, byte>();
        foreach (var c in colors)
        {
            if (c < 0 || exact.ContainsKey(c)) continue;
            if (exact.Count >= room) { exact = null!; break; }
            exact[c] = (byte)(first + exact.Count);
        }
        if (exact != null)
        {
            palette.AddRange(exact.OrderBy(kv => kv.Value).Select(kv => kv.Key));
            for (int i = 0; i < n; i++) indices[i] = colors[i] < 0 ? (byte)0 : exact[colors[i]];
        }
        else
        {
            // Popularity over 15-bit buckets; each used bucket maps to its nearest chosen colour.
            var count = new int[32768];
            var sum = new long[32768 * 3];
            foreach (var c in colors)
            {
                if (c < 0) continue;
                int k = Bucket(c);
                count[k]++; sum[k * 3] += c >> 16 & 255; sum[k * 3 + 1] += c >> 8 & 255; sum[k * 3 + 2] += c & 255;
            }
            var chosen = Enumerable.Range(0, 32768).Where(k => count[k] > 0).OrderByDescending(k => count[k]).Take(room).ToArray();
            var pal = chosen.Select(k => (int)(sum[k * 3] / count[k]) << 16 | (int)(sum[k * 3 + 1] / count[k]) << 8 | (int)(sum[k * 3 + 2] / count[k])).ToArray();
            palette.AddRange(pal);
            var map = new short[32768];
            Array.Fill(map, (short)-1);
            for (int i = 0; i < n; i++)
            {
                int c = colors[i];
                if (c < 0) { indices[i] = 0; continue; }
                int k = Bucket(c);
                if (map[k] < 0) map[k] = (short)(first + Nearest(pal, c));
                indices[i] = (byte)map[k];
            }
        }

        int bits = 1;
        while ((1 << bits) < palette.Count) bits++;
        using var ms = new MemoryStream();
        // Graphic control extension (delay patched in Flush at offsets 4..5).
        ms.Write([0x21, 0xF9, 0x04, (byte)(0x04 | (anyTransparent ? 1 : 0)), 0, 0, 0, 0x00]);
        // Image descriptor + local colour table.
        ms.WriteByte(0x2C);
        void W16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
        W16(r.X); W16(r.Y); W16(r.W); W16(r.H);
        ms.WriteByte((byte)(0x80 | (bits - 1)));
        for (int i = 0; i < 1 << bits; i++)
        {
            int c = i < palette.Count ? palette[i] : 0;
            ms.WriteByte((byte)(c >> 16)); ms.WriteByte((byte)(c >> 8)); ms.WriteByte((byte)c);
        }
        Lzw(indices, Math.Max(2, bits), ms);
        return ms.ToArray();
    }

    private static int Bucket(int c) => (c >> 19 & 31) << 10 | (c >> 11 & 31) << 5 | (c >> 3 & 31);

    private static int Nearest(int[] pal, int c)
    {
        int r = c >> 16 & 255, g = c >> 8 & 255, b = c & 255, best = 0, bestD = int.MaxValue;
        for (int i = 0; i < pal.Length; i++)
        {
            int dr = (pal[i] >> 16 & 255) - r, dg = (pal[i] >> 8 & 255) - g, db = (pal[i] & 255) - b;
            int d = dr * dr * 2 + dg * dg * 4 + db * db * 3;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    /// <summary>GIF LZW (variable code width up to 12 bits), port of the classic Weiner/Thomas encoder.</summary>
    private static void Lzw(byte[] data, int minCodeSize, Stream o)
    {
        o.WriteByte((byte)minCodeSize);
        var block = new byte[256];
        int blockLen = 0;
        void Byte(byte v)
        {
            block[++blockLen] = v;
            if (blockLen == 255) { block[0] = 255; o.Write(block, 0, 256); blockLen = 0; }
        }

        const int maxBits = 12, maxMaxCode = 1 << maxBits;
        int initBits = minCodeSize + 1, nBits = initBits, maxCode = (1 << nBits) - 1;
        int clearCode = 1 << minCodeSize, eofCode = clearCode + 1, freeEnt = clearCode + 2;
        bool clearFlag = false;
        int accum = 0, accBits = 0;
        var dict = new Dictionary<int, int>(4096);

        void Output(int code)
        {
            accum |= code << accBits;
            accBits += nBits;
            while (accBits >= 8) { Byte((byte)accum); accum >>= 8; accBits -= 8; }
            if (freeEnt > maxCode || clearFlag)
            {
                if (clearFlag) { nBits = initBits; maxCode = (1 << nBits) - 1; clearFlag = false; }
                else { nBits++; maxCode = nBits == maxBits ? maxMaxCode : (1 << nBits) - 1; }
            }
            if (code == eofCode)
            {
                while (accBits > 0) { Byte((byte)accum); accum >>= 8; accBits -= 8; }
            }
        }

        Output(clearCode);
        int ent = data.Length > 0 ? data[0] : 0;
        for (int i = 1; i < data.Length; i++)
        {
            int c = data[i];
            int key = (ent << 8) | c;
            if (dict.TryGetValue(key, out var code)) { ent = code; continue; }
            Output(ent);
            ent = c;
            if (freeEnt < maxMaxCode) dict[key] = freeEnt++;
            else
            {
                dict.Clear();
                freeEnt = clearCode + 2;
                clearFlag = true;
                Output(clearCode);
            }
        }
        Output(ent);
        Output(eofCode);
        if (blockLen > 0) { block[0] = (byte)blockLen; o.Write(block, 0, blockLen + 1); }
        o.WriteByte(0);
    }

    private void U16(int v) { _out.WriteByte((byte)v); _out.WriteByte((byte)(v >> 8)); }
    private void Write(ReadOnlySpan<byte> s) => _out.Write(s);

    public void Dispose() => _out.Dispose();
}
