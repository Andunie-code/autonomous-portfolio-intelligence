namespace PortfolioIntelligence.Kernel.Statistics;

/// <summary>Standard normal distribution: CDF (Hart 1968, as given by West 2005; ~1e-15) and its inverse (Acklam, refined once).</summary>
public static class Normal
{
    public static double Cdf(double x)
    {
        var abs = Math.Abs(x);
        double c;
        if (abs > 37)
        {
            c = 0;
        }
        else
        {
            var e = Math.Exp(-abs * abs / 2);
            if (abs < 7.07106781186547)
            {
                var b = 3.52624965998911E-02 * abs + 0.700383064443688;
                b = b * abs + 6.37396220353165;
                b = b * abs + 33.912866078383;
                b = b * abs + 112.079291497871;
                b = b * abs + 221.213596169931;
                b = b * abs + 220.206867912376;
                c = e * b;
                b = 8.83883476483184E-02 * abs + 1.75566716318264;
                b = b * abs + 16.064177579207;
                b = b * abs + 86.7807322029461;
                b = b * abs + 296.564248779674;
                b = b * abs + 637.333633378831;
                b = b * abs + 793.826512519948;
                b = b * abs + 440.413735824752;
                c /= b;
            }
            else
            {
                var b = abs + 0.65;
                b = abs + 4 / b;
                b = abs + 3 / b;
                b = abs + 2 / b;
                b = abs + 1 / b;
                c = e / b / 2.506628274631;
            }
        }

        return x > 0 ? 1 - c : c;
    }

    public static double InverseCdf(double p)
    {
        if (p is <= 0 or >= 1 || double.IsNaN(p))
        {
            throw new ArgumentOutOfRangeException(nameof(p));
        }

        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double low = 0.02425;
        double x;
        if (p < low)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        else if (p <= 1 - low)
        {
            var q = p - 0.5;
            var r = q * q;
            x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        else
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        // One Halley step against the accurate CDF.
        var error = Cdf(x) - p;
        var u = error * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        return x - u / (1 + x * u / 2);
    }
}
