using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mobius
{
    class MobiDecoder
    {
        int _width, _height;
        int _dctTableIndex, _quantizer;
        int[][] _qTable;

        byte[] _pre;
        (int x, int y)[] _motion;
        BitReader br;

        bool _isMoflex;
        string[] MotionPredictTable => _isMoflex ? MotionPredictTableMoflex : MotionPredictTableMods;

        public class Block
        {
            public int Width { get; }
            public int Height { get; }
            public byte[][] Array { get; }
            public int OffsetX { get; private set; }
            public int OffsetY { get; private set; }
            public int Index { get; private set; }

            public Block(int width, int height) : this(width, height, Enumerable.Range(0, height).Select(_ => new byte[width]).ToArray())
            {
            }

            Block(int width, int height, byte[][] array)
            {
                Width = width;
                Height = height;
                Array = array;
            }

            public Block GetSubBlock(int addx, int addy, int size, int index = 0)
            {
                return new Block(size, size, Array)
                {
                    OffsetX = OffsetX + addx * size,
                    OffsetY = OffsetY + addy * size,
                    Index = index
                };
            }

            public byte this[int x, int y]
            {
                get => Array[OffsetY + y][OffsetX + x];
                set => Array[OffsetY + y][OffsetX + x] = value;
            }

            public IEnumerable<Block> Partition(int size)
            {
                return from y in Enumerable.Range(0, Height / size)
                       from x in Enumerable.Range(0, Width / size)
                       select GetSubBlock(x, y, size, y * (Width / size) + x);
            }

            public void Fill(Func<int, int, byte> func)
            {
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        this[x, y] = func(x, y);
            }
        }

        public Block[][] YUV { get; private set; }

        public MobiDecoder(int width, int height)
        {
            _width = width;
            _height = height;
            YUV = new int[6].Select(_ => new[] { new Block(_width, _height), new Block(_width / 2, _height / 2), new Block(_width / 2, _height / 2) }).ToArray();
        }

        public byte[] Decode(Stream stream)
        {
            br = new BitReader(stream, false);
            YUV = YUV.Skip(5).Concat(YUV.Take(5)).ToArray();
            var (yMacroblocks, uBlocks, vBlocks) = (YUV[0][0].Partition(16).ToList(), YUV[0][1].Partition(8).ToList(), YUV[0][2].Partition(8).ToList());

            if (br.Pop()) // I-Frame
            {
                _isMoflex = br.Pop();
                _dctTableIndex = br.PopInt(1);
                SetupQuantizationTables(br.PopInt(6));
                foreach (var macroblock in yMacroblocks)
                    ProcessMacroblock(macroblock, br.Pop());
            }
            else // P-Frame, uses 5 previous frames
            {
                _dctTableIndex = 0;
                SetupQuantizationTables(_quantizer + br.PopSignedExpGolomb());

                _motion = new(int, int)[_width / 16 + 3];

                foreach (var macroblock in yMacroblocks)
                {
                    var (x, y) = (macroblock.OffsetX, macroblock.OffsetY);
                    int MedianOfThree(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                    _motion[0].x = MedianOfThree(_motion[x / 16 + 1].x, _motion[x / 16 + 2].x, _motion[x / 16 + 3].x);
                    _motion[0].y = MedianOfThree(_motion[x / 16 + 1].y, _motion[x / 16 + 2].y, _motion[x / 16 + 3].y);
                    _motion[x / 16 + 2] = (0, 0);

                    var index = br.PopVLC(MotionPredictTable[0]);
                    if (index == 6 || index == 7)
                        ProcessMacroblock(macroblock, index == 7);
                    else
                    {
                        PredictMotion(16, 16, index, x / 16 + 2, x, y);
                        var flags = new BitArray(new[] { PFrameBlock8x8CoefficientTable[br.PopExpGolomb()] });
                        foreach (var block in macroblock.Partition(8))
                            if (flags[block.Index]) AddPFrameCoefficients(block);

                        if (flags[4]) AddPFrameCoefficients(uBlocks[macroblock.Index]);
                        if (flags[5]) AddPFrameCoefficients(vBlocks[macroblock.Index]);
                    }
                }
            }

            var recolor = YUV[0];
            if (!_isMoflex)
            {
                // quick hack for converting mods YUV to the standard YUV
                recolor = YUV[0].Select(channel => new Block(channel.Width, channel.Height)).ToArray();
                for (int y = 0; y < _height / 2; y++)
                    for (int x = 0; x < _width / 2; x++)
                    {
                        var (U, V) = (YUV[0][1][x, y], YUV[0][2][x, y]);
                        recolor[1][x, y] = Clamp(-0.587 * U - 0.582 * V + 277.691);
                        recolor[2][x, y] = Clamp(0.511 * U - 0.736 * V + 156.776);
                        var uvOffset = 0.159 * U + 0.149 * V - 23.456;
                        for (int i = 0; i < 2; i++)
                            for (int j = 0; j < 2; j++)
                                recolor[0][2 * x + j, 2 * y + i] = Clamp(0.859 * YUV[0][0][2 * x + j, 2 * y + i]);
                        byte Clamp(double d) => (byte)Math.Max(0, Math.Min(255, d));
                    }
            }

            return recolor.SelectMany(channel => channel.Array).SelectMany(x => x).ToArray();
        }

        void SetupQuantizationTables(int quantizer)
        {
            _quantizer = quantizer;
            var (qx, qy) = (quantizer % 6, quantizer / 6);
            _qTable = new[]
            {
                ZigZagTable4x4.Zip(Quant4x4[qx], (z, q) => z | (q << qy + 8)).ToArray(),
                ZigZagTable8x8.Zip(Quant8x8[qx], (z, q) => z | (q << qy + 6)).ToArray()
            };
            _pre = Enumerable.Repeat((byte)9, 20).ToArray();
        }

        int GetPModePrediction(Block subblock)
        {
            var index = (subblock.OffsetY & 0xC) | (subblock.OffsetX / 4 % 4);
            var val = (byte)Math.Min(_pre[index], index % 4 == 0 ? 9 : _pre[index + 3]);
            if (val == 9) val = 3;

            if (!br.Pop())
            {
                var x = br.PopInt(3); // change the value: anything but val
                val = (byte)(x + (x >= val ? 1 : 0));
            }

            _pre[index + 4] = val; // 4x4 block below
            if (subblock.Width == 8)
                _pre[index + 5] = _pre[index + 8] = _pre[index + 9] = val; // 8x8 block below
            return val;
        }

        void ProcessMacroblock(Block macroblock, bool predictPMode)
        {
            var flags = new BitArray(new[] { Block8x8CoefficientTable[br.PopExpGolomb()] });

            if (predictPMode)
            {
                foreach (var block in macroblock.Partition(8))
                    ProcessBlock(block, flags[block.Index], GetPModePrediction);
            }
            else
            {
                var pMode = br.PopInt(3);
                if (pMode == 2)
                {
                    PredictIntra(macroblock, pMode, false);
                    pMode = 9;
                }

                foreach (var block in macroblock.Partition(8))
                    ProcessBlock(block, flags[block.Index], _ => pMode);
            }

            var pModeUV = br.PopInt(3);
            var uBlock = YUV[0][1].GetSubBlock(macroblock.OffsetX / 16, macroblock.OffsetY / 16, 8);
            var vBlock = YUV[0][2].GetSubBlock(macroblock.OffsetX / 16, macroblock.OffsetY / 16, 8);

            if (pModeUV == 2)
            {
                PredictIntra(uBlock, pModeUV, false);
                PredictIntra(vBlock, pModeUV, false);
                pModeUV = 9;
            }
            ProcessBlock(uBlock, flags[4], _ => pModeUV);
            ProcessBlock(vBlock, flags[5], _ => pModeUV);
        }

        void ProcessBlock(Block block, bool hasCoefficient, Func<Block, int> pModeFunc)
        {
            if (!hasCoefficient)
            {
                PredictIntra(block, pModeFunc(block), false);
                return;
            }

            var tmp = br.PopExpGolomb();
            if (tmp == 0)
                PredictIntra(block, pModeFunc(block), true);
            else
            {
                var flags = new BitArray(new[] { Block4x4CoefficientTable[tmp - 1] });
                foreach (var subblock in block.Partition(4))
                    PredictIntra(subblock, pModeFunc(subblock), flags[subblock.Index]);
            }
        }

        void PredictIntra(Block block, int pMode, bool addCoefficient)
        {
            var size = block.Width;
            byte pget(int x, int y)
            {
                if (x == -1 && y >= size) return block[-1, size - 1];
                if (x >= -1 && y >= -1) return block[x, y];
                if (x == -1 && y == -2) return block[0, -1];
                if (x == -2 && y == -1) return block[-1, 0];
                throw new Exception();
            }
            byte Half(params byte[] bs) => (byte)((bs.Sum(b => b) * 2 / bs.Length + 1) / 2);
            byte Half3(byte a, byte b, byte c) => Half(a, b, b, c);
            byte HalfHorz(int x, int y) => Half3(pget(x - 1, y), pget(x, y), pget(x + 1, y));
            byte HalfVert(int x, int y) => Half3(pget(x, y - 1), pget(x, y), pget(x, y + 1));

            switch (pMode)
            {
                case 0:
                    block.Fill((x, y) => pget(x, -1));
                    break;
                case 1:
                    block.Fill((x, y) => pget(-1, y));
                    break;
                case 2:
                    {
                        var top = Enumerable.Range(0, size).Select(x => block[x, -1]).ToList(); // row of pixels above it
                        var left = Enumerable.Range(0, size).Select(y => block[-1, y]).ToList(); // col of pixels to left
                        int bottommost = left.Last();
                        int rightmost = top.Last();

                        int Adjust(int x) => size == 16 ? (x + 1) >> 1 : x;
                        int avg = (bottommost + rightmost + 1) / 2 + 2 * br.PopSignedExpGolomb();
                        int r6 = Adjust(avg - bottommost);
                        int r9 = Adjust(avg - rightmost);
                        var shift = Adjust(size) == 8 ? 3 : 2;

                        var arr1 = top.Select((val, x) => ((bottommost - val) << shift) + r6 * (x + 1)).Select(Adjust).ToList();
                        var arr2 = left.Select((val, y) => ((rightmost - val) << shift) + r9 * (y + 1)).Select(Adjust).ToList();
                        block.Fill((x, y) => (byte)(((top[x] + left[y] + ((arr1[x] * (y + 1) + arr2[y] * (x + 1)) >> 2 * shift)) + 1) / 2));
                    }
                    break;
                case 3:
                    {
                        var left = block.OffsetX == 0 ? new byte[0] : Enumerable.Range(0, size).Select(i => block[-1, i]);
                        var top = block.OffsetY == 0 ? new byte[0] : Enumerable.Range(0, size).Select(i => block[i, -1]);
                        var average = Half(left.Concat(top).DefaultIfEmpty((byte)0x80).ToArray());
                        block.Fill((x, y) => average);
                    }
                    break;
                case 4:
                    block.Fill((x, y) => x % 2 == 0 ? Half(pget(-1, y + x / 2), pget(-1, y + x / 2 + 1))
                                                    : HalfVert(-1, y + x / 2 + 1));
                    break;
                case 5:
                    block.Fill((x, y) => x == 0 ? Half(pget(-1, y - 1), pget(-1, y))
                                       : y == 0 ? HalfHorz(x - 2, y - 1)
                                       : x == 1 ? HalfVert(x - 2, y - 1)
                                       : pget(x - 2, y - 1));
                    break;
                case 6:
                    block.Fill((x, y) => y == 0 ? Half(pget(x - 1, -1), pget(x, -1))
                                       : x == 0 ? HalfVert(x - 1, y - 2)
                                       : y == 1 ? HalfHorz(x - 1, y - 2)
                                       : pget(x - 1, y - 2));
                    break;
                case 7:
                    block.Fill((x, y) =>
                    {
                        var clr = pget(x - 1, y - 1);
                        if (x != 0 && y != 0) return clr;
                        var acc1 = x == 0 ? pget(-1, y) : pget(x - 2, -1);
                        var acc2 = y == 0 ? pget(x, -1) : pget(-1, y - 2);
                        return Half3(acc1, clr, acc2);
                    });
                    break;
                case 8:
                    block.Fill((x, y) => y == 0 ? Half(pget(x, -1), pget(x + 1, -1))
                                       : y == 1 ? HalfHorz(x + 1, y - 2)
                                       : x < size - 1 ? pget(x + 1, y - 2)
                                       : y % 2 == 0 ? Half(pget(y / 2 + size - 1, -1), pget(y / 2 + size, -1))
                                       : HalfHorz(y / 2 + size, -1));
                    break;
            }

            if (addCoefficient)
                AddCoefficients(block);
        }

        void PredictMotion(int width, int height, int index, int offsetm, int offsetx, int offsety)
        {
            if (index <= 5)
            {
                var (dx, dy) = _motion[0];
                if (index > 0) (dx, dy) = (dx + br.PopSignedExpGolomb(), dy + br.PopSignedExpGolomb());
                var srcFrame = Math.Max(1, index);
                _motion[offsetm] = (dx, dy);
                for (int i = 0; i < 3; i++)
                {
                    if (i == 1)
                        (offsetx, offsety, dx, dy, width, height) = (offsetx >> 1, offsety >> 1, dx >> 1, dy >> 1, width >> 1, height >> 1);
                    var method = (dx & 1) | ((dy & 1) << 1);
                    var src = YUV[Math.Max(1, index)][i].GetSubBlock(offsetx + (dx >> 1), offsety + (dy >> 1), 1);
                    var dst = YUV[0][i].GetSubBlock(offsetx, offsety, 1);
                    for (int y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                        {
                            dst[x, y] = method == 0 ? src[x, y]
                                      : method == 1 ? (byte)((src[x, y] >> 1) + (src[x + 1, y] >> 1))
                                      : method == 2 ? (byte)((src[x, y] >> 1) + (src[x, y + 1] >> 1))
                                      : (byte)((((src[x, y] >> 1) + (src[x + 1, y] >> 1)) >> 1) + (((src[x, y + 1] >> 1) + (src[x + 1, y + 1] >> 1)) >> 1));
                        }
                }
            }
            else
            {
                int Index(int x) => x == 16 ? 0 : x == 8 ? 1 : x == 4 ? 2 : x == 2 ? 3 : throw new Exception();
                var (adjx, adjy) = index == 8 ? (0, height / 2) : (width / 2, 0);
                (width, height) = (width - adjx, height - adjy);
                var table = MotionPredictTable[Index(height) * 4 + Index(width)];
                for (int i = 0; i < 2; i++)
                    PredictMotion(width, height, br.PopVLC(table), offsetm, offsetx + i * adjx, offsety + i * adjy);
            }
        }

        void AddPFrameCoefficients(Block block)
        {
            var tmp = br.PopExpGolomb();
            if (tmp == 0)
                AddCoefficients(block);
            else
            {
                var flags = new BitArray(new[] { PFrameBlock4x4CoefficientTable[tmp] });
                foreach (var subblock in block.Partition(4))
                    if (flags[subblock.Index]) AddCoefficients(subblock);
            }
        }

        void AddCoefficients(Block block)
        {
            var size = block.Width;
            var mat = new int[size].Select(_ => new int[size]).ToList();

            (bool last, int run, int level) ReadRunEncoding()
            {
                var n = br.PopVLC(HuffmanTree[_dctTableIndex]);
                return ((n >> 15) == 1, (n >> 9) & 0x3F, (n >> 4) & 0x1F);
            }

            for (var pos = 0; ; pos++)
            {
                var (last, run, level) = ReadRunEncoding();
                if (level != 0)
                {
                    if (br.Pop()) level *= -1;
                }
                else if (!br.Pop())
                {
                    (last, run, level) = ReadRunEncoding();
                    level += RunResidue[_dctTableIndex][(last ? 64 : 0) + run];
                    if (br.Pop()) level *= -1;
                }
                else if (!br.Pop())
                {
                    (last, run, level) = ReadRunEncoding();
                    run += RunResidue[_dctTableIndex][128 + (last ? 64 : 0) + level];
                    if (br.Pop()) level *= -1;
                }
                else
                {
                    (last, run, level) = (br.Pop(), br.PopInt(6), br.PopSignedInt(12));
                }

                pos += run;
                var qval = _qTable[size / 4 - 1][pos];
                mat[(byte)qval / size][(byte)qval % size] = (qval >> 8) * level;

                if (last) break;
            }

            mat[0][0] += 32;
            for (int y = 0; y < size; y++) mat[y] = IDCT(mat[y]); // DCT each row

            for (int y = 0; y < size; y++)
            {
                for (int x = y + 1; x < size; x++)
                    (mat[x][y], mat[y][x]) = (mat[y][x], mat[x][y]);
                mat[y] = IDCT(mat[y]);
                for (int x = 0; x < size; x++)
                    block[x, y] = (byte)Math.Max(0, Math.Min(255, block[x, y] + (mat[y][x] >> 6)));
            }

            int[] IDCT(int[] arr)
            {
                if (size == 4) return Inverse4(arr);
                int[] Inverse4(params int[] rs)
                {
                    var (a, b) = (rs[0] + rs[2], rs[0] - rs[2]);
                    var (c, d) = (rs[1] + (rs[3] >> 1), (rs[1] >> 1) - rs[3]);
                    return new[] { a + c, b + d, b - d, a - c };
                }

                var tmp = Inverse4(arr[0], arr[2], arr[4], arr[6]);
                var (e, f) = (arr[7] + arr[1] - arr[3] - (arr[3] >> 1), arr[7] - arr[1] + arr[5] + (arr[5] >> 1));
                var (g, h) = (arr[5] - arr[3] - arr[7] - (arr[7] >> 1), arr[5] + arr[3] + arr[1] + (arr[1] >> 1));
                var (x3, x2, x1, x0) = (g + (h >> 2), e + (f >> 2), (e >> 2) - f, h - (g >> 2));

                return new[]
                {
                    tmp[0] + x0, tmp[1] + x1, tmp[2] + x2, tmp[3] + x3,
                    tmp[3] - x3, tmp[2] - x2, tmp[1] - x1, tmp[0] - x0
                };
            }
        }

        #region Constants and stuff
        static byte[] ZigZagTable4x4 = (from y in Enumerable.Range(0, 4)
                                        from x in Enumerable.Range(0, 4)
                                        orderby x + y, (x + y) % 2 * x
                                        select (byte)(4 * y + x)).ToArray();

        static byte[] ZigZagTable8x8 = (from y in Enumerable.Range(0, 8)
                                        from x in Enumerable.Range(0, 8)
                                        orderby x + y, (x + y + 1) % 2 * x
                                        select (byte)(8 * y + x)).ToArray();

        static readonly int[][] Quant4x4 =
        {
            new[] { 10, 13, 13, 10, 16, 10, 13, 13, 13, 13, 16, 10, 16, 13, 13, 16 },
            new[] { 11, 14, 14, 11, 18, 11, 14, 14, 14, 14, 18, 11, 18, 14, 14, 18 },
            new[] { 13, 16, 16, 13, 20, 13, 16, 16, 16, 16, 20, 13, 20, 16, 16, 20 },
            new[] { 14, 18, 18, 14, 23, 14, 18, 18, 18, 18, 23, 14, 23, 18, 18, 23 },
            new[] { 16, 20, 20, 16, 25, 16, 20, 20, 20, 20, 25, 16, 25, 20, 20, 25 },
            new[] { 18, 23, 23, 18, 29, 18, 23, 23, 23, 23, 29, 18, 29, 23, 23, 29 },
        };

        static readonly int[][] Quant8x8 =
        {
            new[] {
                20, 19, 19, 25, 18, 25, 19, 24, 24, 19, 20, 18, 32, 18, 20, 19, 19, 24, 24, 19, 19, 25, 18, 25, 18, 25, 18, 25, 19, 24, 24, 19,
                19, 24, 24, 19, 18, 32, 18, 20, 18, 32, 18, 24, 24, 19, 19, 24, 24, 18, 25, 18, 25, 18, 19, 24, 24, 19, 18, 32, 18, 24, 24, 18
            },
            new[] {
                22, 21, 21, 28, 19, 28, 21, 26, 26, 21, 22, 19, 35, 19, 22, 21, 21, 26, 26, 21, 21, 28, 19, 28, 19, 28, 19, 28, 21, 26, 26, 21,
                21, 26, 26, 21, 19, 35, 19, 22, 19, 35, 19, 26, 26, 21, 21, 26, 26, 19, 28, 19, 28, 19, 21, 26, 26, 21, 19, 35, 19, 26, 26, 19
            },
            new[] {
                26, 24, 24, 33, 23, 33, 24, 31, 31, 24, 26, 23, 42, 23, 26, 24, 24, 31, 31, 24, 24, 33, 23, 33, 23, 33, 23, 33, 24, 31, 31, 24,
                24, 31, 31, 24, 23, 42, 23, 26, 23, 42, 23, 31, 31, 24, 24, 31, 31, 23, 33, 23, 33, 23, 24, 31, 31, 24, 23, 42, 23, 31, 31, 23
            },
            new[] {
                28, 26, 26, 35, 25, 35, 26, 33, 33, 26, 28, 25, 45, 25, 28, 26, 26, 33, 33, 26, 26, 35, 25, 35, 25, 35, 25, 35, 26, 33, 33, 26,
                26, 33, 33, 26, 25, 45, 25, 28, 25, 45, 25, 33, 33, 26, 26, 33, 33, 25, 35, 25, 35, 25, 26, 33, 33, 26, 25, 45, 25, 33, 33, 25
            },
            new[] {
                32, 30, 30, 40, 28, 40, 30, 38, 38, 30, 32, 28, 51, 28, 32, 30, 30, 38, 38, 30, 30, 40, 28, 40, 28, 40, 28, 40, 30, 38, 38, 30,
                30, 38, 38, 30, 28, 51, 28, 32, 28, 51, 28, 38, 38, 30, 30, 38, 38, 28, 40, 28, 40, 28, 30, 38, 38, 30, 28, 51, 28, 38, 38, 28
            },
            new[] {
                36, 34, 34, 46, 32, 46, 34, 43, 43, 34, 36, 32, 58, 32, 36, 34, 34, 43, 43, 34, 34, 46, 32, 46, 32, 46, 32, 46, 34, 43, 43, 34,
                34, 43, 43, 34, 32, 58, 32, 36, 32, 58, 32, 43, 43, 34, 34, 43, 43, 32, 46, 32, 46, 32, 34, 43, 43, 34, 32, 58, 32, 43, 43, 32
            }
        };

        static byte[] Block4x4CoefficientTable = { 15, 0, 2, 1, 4, 8, 12, 3, 11, 13, 14, 7, 10, 5, 9, 6 };

        static byte[] PFrameBlock4x4CoefficientTable = { 0, 4, 1, 8, 2, 12, 3, 5, 10, 15, 7, 13, 14, 11, 9, 6 };

        static byte[] Block8x8CoefficientTable =
        {
            0x00, 0x1F, 0x3F, 0x0F, 0x08, 0x04, 0x02, 0x01, 0x0B, 0x0E, 0x1B, 0x0D, 0x03, 0x07, 0x0C, 0x17,
            0x1D, 0x0A, 0x1E, 0x05, 0x10, 0x2F, 0x37, 0x3B, 0x13, 0x3D, 0x3E, 0x09, 0x1C, 0x06, 0x15, 0x1A,
            0x33, 0x11, 0x12, 0x14, 0x18, 0x20, 0x3C, 0x35, 0x19, 0x16, 0x3A, 0x30, 0x31, 0x32, 0x27, 0x34,
            0x2B, 0x2D, 0x39, 0x38, 0x23, 0x36, 0x2E, 0x21, 0x25, 0x22, 0x24, 0x2C, 0x2A, 0x28, 0x29, 0x26
        };

        static byte[] PFrameBlock8x8CoefficientTable =
        {
            0x00, 0x0F, 0x04, 0x01, 0x08, 0x02, 0x0C, 0x03, 0x05, 0x0A, 0x0D, 0x07, 0x0E, 0x0B, 0x1F, 0x09,
            0x06, 0x10, 0x3F, 0x1E, 0x17, 0x1D, 0x1B, 0x1C, 0x13, 0x18, 0x1A, 0x12, 0x11, 0x14, 0x15, 0x20,
            0x2F, 0x16, 0x19, 0x37, 0x3D, 0x3E, 0x3B, 0x3C, 0x33, 0x35, 0x21, 0x24, 0x22, 0x28, 0x23, 0x2C,
            0x30, 0x27, 0x2D, 0x25, 0x3A, 0x2B, 0x2E, 0x2A, 0x31, 0x34, 0x38, 0x32, 0x29, 0x26, 0x39, 0x36
        };

        static readonly Dictionary<int, ushort>[] HuffmanTree = new[]
        {
            new Dictionary<int, ushort>
            {
                [512] = 0x0001, [2052] = 0x822C, [2053] = 0x803C, [2054] = 0x00BC, [2055] = 0x00AC, [1028] = 0xB81B,
                [1029] = 0xB61B, [1030] = 0xB41B, [1031] = 0xB21B, [1032] = 0x122B, [1033] = 0x102B, [1034] = 0x0E2B,
                [1035] = 0x0C2B, [1036] = 0x0A2B, [1037] = 0x063B, [1038] = 0x043B, [1039] = 0x024B, [2080] = 0x00CC,
                [2081] = 0x025C, [2082] = 0x2E1C, [2083] = 0x301C, [2084] = 0xBA1C, [2085] = 0xBC1C, [2086] = 0xBE1C,
                [2087] = 0xC01C, [4176] = 0x026D, [4177] = 0x044D, [4178] = 0x083D, [4179] = 0x0A3D, [4180] = 0x0C3D,
                [4181] = 0x142D, [4182] = 0x321D, [4183] = 0x341D, [4184] = 0xC21D, [4185] = 0xC41D, [4186] = 0xC61D,
                [4187] = 0xC81D, [4188] = 0xCA1D, [4189] = 0xCC1D, [4190] = 0xCE1D, [4191] = 0xD01D, [131] = 0x0001,
                [1056] = 0x009B, [1057] = 0x008B, [529] = 0xB01A, [530] = 0xAE1A, [531] = 0xAC1A, [532] = 0xAA1A,
                [533] = 0xA81A, [534] = 0xA61A, [535] = 0xA41A, [536] = 0xA21A, [537] = 0x802A, [538] = 0x2C1A,
                [539] = 0x2A1A, [540] = 0x281A, [541] = 0x261A, [542] = 0x241A, [543] = 0x221A, [544] = 0x201A,
                [545] = 0x1E1A, [546] = 0x082A, [547] = 0x062A, [548] = 0x007A, [549] = 0x006A, [275] = 0xA019,
                [276] = 0x9E19, [277] = 0x9C19, [278] = 0x9A19, [279] = 0x9819, [280] = 0x9619, [281] = 0x9419,
                [282] = 0x9219, [283] = 0x1C19, [284] = 0x1A19, [285] = 0x0429, [286] = 0x0239, [287] = 0x0059,
                [144] = 0x9018, [145] = 0x8E18, [146] = 0x8C18, [147] = 0x8A18, [148] = 0x1818, [149] = 0x1618,
                [150] = 0x1418, [151] = 0x0048, [76] = 0x8817, [77] = 0x8617, [78] = 0x8417, [79] = 0x8217,
                [80] = 0x1217, [81] = 0x1017, [82] = 0x0E17, [83] = 0x0C17, [84] = 0x0227, [85] = 0x0037, [43] = 0x0A16,
                [44] = 0x0816, [45] = 0x0616, [23] = 0x8015, [6] = 0x0013, [14] = 0x0214, [30] = 0x0415, [31] = 0x0025
            },
            new Dictionary<int, ushort>
            {
                [512] = 0x0001, [2052] = 0x807C, [2053] = 0x806C, [2054] = 0x016C, [2055] = 0x015C, [1028] = 0x842B,
                [1029] = 0x823B, [1030] = 0x805B, [1031] = 0x1A1B, [1032] = 0x0A3B, [1033] = 0x102B, [1034] = 0x083B,
                [1035] = 0x064B, [1036] = 0x044B, [1037] = 0x027B, [1038] = 0x014B, [1039] = 0x013B, [2080] = 0x017C,
                [2081] = 0x018C, [2082] = 0x028C, [2083] = 0x122C, [2084] = 0x862C, [2085] = 0x882C, [2086] = 0x9E1C,
                [2087] = 0xA01C, [4176] = 0x019D, [4177] = 0x01AD, [4178] = 0x01BD, [4179] = 0x029D, [4180] = 0x0C3D,
                [4181] = 0x02AD, [4182] = 0x045D, [4183] = 0x0E3D, [4184] = 0x1C1D, [4185] = 0x808D, [4186] = 0x8A2D,
                [4187] = 0x8C2D, [4188] = 0xA21D, [4189] = 0xA41D, [4190] = 0xA61D, [4191] = 0xA81D, [131] = 0x0001,
                [1056] = 0x012B, [1057] = 0x011B, [529] = 0x9C1A, [530] = 0x9A1A, [531] = 0x981A, [532] = 0x961A,
                [533] = 0x941A, [534] = 0x822A, [535] = 0x804A, [536] = 0x181A, [537] = 0x161A, [538] = 0x0E2A,
                [539] = 0x0C2A, [540] = 0x0A2A, [541] = 0x063A, [542] = 0x043A, [543] = 0x026A, [544] = 0x025A,
                [545] = 0x010A, [546] = 0x082A, [547] = 0x00FA, [548] = 0x00EA, [549] = 0x00DA, [275] = 0x9019,
                [276] = 0x8E19, [277] = 0x8C19, [278] = 0x8039, [279] = 0x1419, [280] = 0x1219, [281] = 0x1019,
                [282] = 0x9219, [283] = 0x0629, [284] = 0x0249, [285] = 0x00C9, [286] = 0x00B9, [287] = 0x00A9,
                [144] = 0x8818, [145] = 0x8618, [146] = 0x0C18, [147] = 0x8A18, [148] = 0x0E18, [149] = 0x0428,
                [150] = 0x0238, [151] = 0x0098, [76] = 0x8027, [77] = 0x0A17, [78] = 0x8417, [79] = 0x8217,
                [80] = 0x0817, [81] = 0x0617, [82] = 0x0087, [83] = 0x0077, [84] = 0x0227, [85] = 0x0067, [43] = 0x0416,
                [44] = 0x0056, [45] = 0x0046, [23] = 0x8015, [6] = 0x0013, [14] = 0x0024, [30] = 0x0215, [31] = 0x0035
            }
        };

        static readonly int[][] RunResidue =
        {
            new[]
            {
                12,  6,  4,  3,  3,  3,  3,  2,  2,  2,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  0,  0,  0,  0,  0,
                 0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 3,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 1, 27, 11,  7,  3,  2,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1, 41,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1
            },
            new[]
            {
                27, 10,  5,  4,  3,  3,  3,  3,  2,  2,  1,  1,  1,  1,  1,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 8,  3,  2,  2,  2,  2,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 1, 15, 10,  8,  4,  3,  2,  2,  2,  2,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1, 21,  7,  2,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1
            }
        };

        static readonly string[] MotionPredictTableMods =
        {
            "....1..0..89.............27.....................43....56........",
            "....0..1..9.2.........54..38....",
            ".....0.13....2....95....48......",
            "..1.........3.20..........4...........................85........",
            "....0..1...82.......54....39....",
            "....0..1..3..2........5948......",
            "....0..1..3..2........9584......",
            "....02.1....3.............4...........................85........",
            "....0..1..3.2.........84..59....",
            "....2..1...3.0......89..54......",
            "....0..1..43.2...........5......................89..............",
            "....0..1..4.32........85........",
            "..1..........20.........94....53",
            "....2..1..4.30........95........",
            "....0..1..4.32........95........",
            ".....10.54....32"
        };

        static readonly string[] MotionPredictTableMoflex =
        {
            "...0....8.1.......2....9..............36....7.............................................54....................................",
            "....9.1...2...80......3.......................54................",
            "....0..1...29.......54....38....",
            "..1..........2.0........54..83..",
            ".....8.129...0..........3.........................54............",
            ".......13.2980....54............",
            "..1.........20..............983...............................54",
            "..1..........20.........85....43",
            "....0..1...28.......54....39....",
            "..1.........20..............983...............................54",
            "....0..1..3..2........9854......",
            "....0..1..43.2..........85......",
            "..1..........20.........54....93",
            "..1..........20.........95....43",
            "....0..1..53.2..........94......",
            "....0..1..4532.."
        };
        #endregion
    }
}
