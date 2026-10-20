namespace Backbloom.Capture.Hdr;

/// <summary>
/// The built-in HDR to SDR table (what <c>lut_lin_tables.3d</c> holds when the user hasn't installed a calibrated one):
/// maps HDR10 (PQ, BT.2020) RGB captured as 8-bit to SDR sRGB. Port of utils/DefaultLut.cpp; the arithmetic follows
/// the C++ code operation by operation so the table is bit-identical (checked against its MD5 in the tests).
/// </summary>
public static class DefaultLut
{
    // Calibration of the default table (PQ transfer, BT.601 limited range YUV as intermediate format, BT.2020 primaries
    // converted with an adjusted matrix and a per-channel color aspect correction). Matrices are column-major, as in
    // linalg: each row of these arrays is one column.
    private static readonly double[,] s_yuvToLinearRgb =
    {
        { 1.16438356164, 1.16438356164, 1.16438356164, 0 },
        { -4.43119136738e-17, -0.387076923077, 2.02178571429, 0 },
        { 1.58691964286, -0.821942994505, -1.77247654695e-16, 0 },
        { -0.869630789302, 0.53382122535, -1.08791650359, 1 },
    };

    private static readonly double[,] s_bt2020ToSrgb =
    {
        { 1.62995144161, -0.159968936426, -0.0191389994477 },
        { -0.556261837808, 1.17281602107, -0.104271698501 },
        { -0.0736896038051, -0.0128470846442, 1.12341069795 },
    };

    private static readonly double[] s_yuvAspect = [0.9925, 1, 1.035];
    private static readonly double[] s_colorAspect = [1.06441799482, 1.00700595321, 1.06831049137];
    private static readonly double[,] s_rgbToYuv = RgbToYuvMatrix();

    private const double Nits = 229.625687;

    /// <summary>Builds the table (all cores; about a second).</summary>
    public static byte[] Create()
    {
        var table = GC.AllocateUninitializedArray<byte>(ToneMappingLut.TableSize);
        Parallel.For(0, 256, b =>
        {
            Span<byte> result = stackalloc byte[3];
            for (var g = 0; g < 256; g++)
            {
                for (var r = 0; r < 256; r++)
                {
                    ToneMap(r, g, b, result);
                    result.CopyTo(table.AsSpan(ToneMappingLut.Index(r, g, b), 3));
                }
            }
        });
        return table;
    }

    /// <summary>
    /// Writes the table to <paramref name="directory"/>/lut_lin_tables.3d through a temporary file, so a reader never
    /// sees a partial table.
    /// </summary>
    public static string CreateFile(string directory, byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, LutLoader.FileName);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, table.AsSpan(0, ToneMappingLut.TableSize));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Tone maps one 8-bit RGB value (port of the anonymous toneMap function).</summary>
    internal static void ToneMap(int r, int g, int b, Span<byte> result)
    {
        Span<double> yuv = stackalloc double[3];
        Transform(s_rgbToYuv, r / 255.0, g / 255.0, b / 255.0, yuv);

        var white = RoundTo0To255(yuv[1] * 255.0) == RoundTo0To255(yuv[2] * 255.0) && RoundTo0To255(yuv[1] * 255.0) == 128;

        const double Low = 16.0 / 255.0;
        yuv[0] = (yuv[0] < Low) ? Low : ((yuv[0] - Low) * s_yuvAspect[0]) + Low;

        if (!white)
        {
            const double Mid = 128.0 / 256.0;
            yuv[1] = ((yuv[1] - Mid) * s_yuvAspect[1]) + Mid;
            yuv[2] = ((yuv[2] - Mid) * s_yuvAspect[2]) + Mid;
        }

        Span<double> rgb = stackalloc double[3];
        Transform(s_yuvToLinearRgb, yuv[0], yuv[1], yuv[2], rgb);
        const double Scale = 10000.0 / Nits;
        var lr = PqToLinear(Scale, rgb[0]);
        var lg = PqToLinear(Scale, rgb[1]);
        var lb = PqToLinear(Scale, rgb[2]);

        var m = s_bt2020ToSrgb;
        var sr = SrgbLinearToNonlinear((m[0, 0] * lr) + (m[1, 0] * lg) + (m[2, 0] * lb));
        var sg = SrgbLinearToNonlinear((m[0, 1] * lr) + (m[1, 1] * lg) + (m[2, 1] * lb));
        var sb = SrgbLinearToNonlinear((m[0, 2] * lr) + (m[1, 2] * lg) + (m[2, 2] * lb));

        if (!white)
        {
            sr *= s_colorAspect[0];
            sg *= s_colorAspect[1];
            sb *= s_colorAspect[2];
        }
        else
        {
            var average = (s_colorAspect[0] + s_colorAspect[1] + s_colorAspect[2]) / 3.0;
            sr *= average;
            sg *= average;
            sb *= average;
        }

        result[0] = (byte)RoundTo0To255(sr * 255.0);
        result[1] = (byte)RoundTo0To255(sg * 255.0);
        result[2] = (byte)RoundTo0To255(sb * 255.0);
    }

    /// <summary>PQ (SMPTE ST 2084) EOTF scaled so that 1.0 is 10000 nits / scale (ColorSpaceMath::PQ_ST2084).</summary>
    internal static double PqToLinear(double scale, double nonlinear)
    {
        const double M1 = 2610.0 / 16384.0;
        const double M2 = 2523.0 / 4096.0 * 128.0;
        const double C1 = 3424.0 / 4096.0;
        const double C2 = 2413.0 / 4096.0 * 32.0;
        const double C3 = 2392.0 / 4096.0 * 32.0;
        const double DblMin = 2.2250738585072014e-308;

        if (nonlinear > 0.0)
        {
            var xpow = Math.Pow(nonlinear, 1.0 / M2);
            var num = Math.Max(xpow - C1, 0.0);
            var den = Math.Max(C2 - (C3 * xpow), DblMin);
            nonlinear = Math.Pow(num / den, 1.0 / M1);
        }
        else if (nonlinear < 0.0)
        {
            return -PqToLinear(scale, -nonlinear);
        }
        else
        {
            return 0;
        }

        return scale * nonlinear;
    }

    /// <summary>ColorSpaceMath::srgb_linear_to_nonlinear (its constants are floats widened to double).</summary>
    internal static double SrgbLinearToNonlinear(double input)
    {
        const double Alpha = (double)1.055010718947587f;
        const double Beta = (double)0.003041282560128f;

        input = Math.Max(input, 0.0);
        return input < Beta ? input * 12.92 : (Alpha * Math.Pow(input, 1.0 / 2.4)) - (Alpha - 1.0);
    }

    /// <summary>round_to_0_255: clamp, then std::lround (halves away from zero).</summary>
    private static int RoundTo0To255(double value) => (int)Math.Round(Math.Clamp(value, 0, 255), MidpointRounding.AwayFromZero);

    /// <summary>linalg mul(double4x4, double4(x, y, z, 1)), summed column by column like linalg.</summary>
    private static void Transform(double[,] m, double x, double y, double z, Span<double> result)
    {
        for (var i = 0; i < 3; i++)
        {
            result[i] = (m[0, i] * x) + (m[1, i] * y) + (m[2, i] * z) + (m[3, i] * 1.0);
        }
    }

    /// <summary>RGB to limited range BT.601 YUV (rgbToYuvMatrix), column-major.</summary>
    private static double[,] RgbToYuvMatrix()
    {
        const double Kr = 0.2990;
        const double Kb = 0.1140;
        var kg = 1.0 - Kr - Kb;
        var cr = 0.5 / (1.0 - Kb);
        var cb = 0.5 / (1.0 - Kr);
        double scaleY = 219 / 255.0, addY = 16 / 255.0, scaleUV = 224 / 255.0, addUV = 128 / 255.0;

        return new double[,]
        {
            { Kr * scaleY, -Kr * cr * scaleUV, (1 - Kr) * cb * scaleUV, 0 },
            { kg * scaleY, -kg * cr * scaleUV, -kg * cb * scaleUV, 0 },
            { Kb * scaleY, (1 - Kb) * cr * scaleUV, -Kb * cb * scaleUV, 0 },
            { addY, addUV, addUV, 1 },
        };
    }
}
