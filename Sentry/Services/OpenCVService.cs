using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using OpenCvSharp.Internal;
using Size = OpenCvSharp.Size;

namespace OpenShock.Sentry.Services;

public sealed class OpenCVService
{
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    public async Task Start()
    {
        var sentryModuleDirectory = Path.GetDirectoryName(typeof(SentryModule).Assembly.Location);
        if (sentryModuleDirectory == null)
            throw new ArgumentNullException(nameof(sentryModuleDirectory), "Sentry module directory not found");
        var openCvNativeLibs = Path.Combine(sentryModuleDirectory, "libs", "opencv_native");
        
        WindowsLibraryLoader.Instance.AdditionalPaths.Add(openCvNativeLibs);
        
        if(Environment.OSVersion.Platform != PlatformID.Win32NT) return;
        // … your setup up to orb/template loading stays the same …

        // Pre-compute template once
        using var templateMat = Cv2.ImRead(@"C:\Users\Lucpe\Desktop\mc6.png", ImreadModes.Grayscale);
        using var orb = ORB.Create();
        var descTemplate = new Mat();
        orb.DetectAndCompute(templateMat, null, out var kpTemplate, descTemplate);

        // Prepare reusable buffers
        var bounds = new Rectangle(0, 0, 2560, 1440);
        using var captureBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(captureBitmap);
        using var srcMat = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC4);
        using var grayMat = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1);

        Console.WriteLine("Template features ready. Scanning...");

        while (true)
        {
            // 1) Grab screen into the same Bitmap
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

            // 2) Convert Bitmap → Mat, then to grayscale correctly
            captureBitmap.ToMat(srcMat);
            Cv2.CvtColor(srcMat, grayMat, ColorConversionCodes.BGRA2GRAY);

            // 3) Try to match
            var found = FindImage(orb, grayMat, kpTemplate, descTemplate, templateMat.Size(), out var rect);
            if (found)
            {
                Console.WriteLine($"Found at {rect}"); 
            }
            else
            {
                Console.WriteLine("No match.");
            }
            
            if(rect != default) Cv2.Rectangle(srcMat, rect, found ? Scalar.LimeGreen : Scalar.Orange, 3);

            // 4) Show & process events **every** frame
            Cv2.ImShow("Screen Match", srcMat);
            if (Cv2.WaitKey(1) == 27)  // ESC
                break;
        }
        Cv2.DestroyAllWindows();
    }
    
    static bool FindImage(
        ORB orb,
        Mat sceneGray,
        KeyPoint[] kpTemplate,
        Mat descTemplate,
        Size tplSize,
        out Rect found)
    {
        var isFound = true;
        found = default;

        // 1) detect scene features
        var descScene = new Mat();
        orb.DetectAndCompute(sceneGray, null, out var kpScene, descScene);
        if (descScene.Empty()) return false;

        // 2) match & filter by distance (tune 50→30 for stricter, 75 for looser)
        var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: true);
        var matches = matcher.Match(descTemplate, descScene)
            .Where(m => m.Distance < 40)
            .ToArray();
        if (matches.Length < 8)  // need at least 8 good matches
            isFound = false;

        Console.WriteLine(
            $"TplKP={kpTemplate.Length}, SceneKP={kpScene.Length}, Good={matches.Length}");

        if(matches.Length < 4) return false;
        
        // 3) homography
        var srcPts = matches.Select(m => kpTemplate[m.QueryIdx].Pt).ToArray();
        var dstPts = matches.Select(m => kpScene[m.TrainIdx].Pt).ToArray();
        using var H = Cv2.FindHomography(InputArray.Create(srcPts),
            InputArray.Create(dstPts),
            HomographyMethods.Ransac);
        if (H.Empty()) isFound = false;

        // 4) warp corners → bounding rect
        var corners = new[]
        {
            new Point2f(0,0),
            new Point2f(tplSize.Width,0),
            new Point2f(tplSize.Width,tplSize.Height),
            new Point2f(0,tplSize.Height)
        };
        var sceneCorners = Cv2.PerspectiveTransform(corners, H);
        found = Cv2.BoundingRect(sceneCorners);
        return isFound;
    }
}