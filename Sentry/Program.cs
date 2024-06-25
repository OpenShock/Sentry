// using System.Diagnostics;
// using System.Drawing;
// using System.Drawing.Imaging;
// using OpenCvSharp;
//
// var templateMat = new Mat("C:\\Users\\Lucpe\\Desktop\\7Nkh08xddz.png", ImreadModes.Grayscale);
//
// var captureBitmap = new Bitmap(2560, 1440, PixelFormat.Format32bppArgb);
// var captureRectangle = new Rectangle(0, 0, 2560, 1440);
// var captureGraphics = Graphics.FromImage(captureBitmap);
//
// var srcMat = new Mat(captureBitmap.Height, captureBitmap.Width, MatType.CV_8UC4);;
// var betterSrcMat = new Mat();
// var matchResult = new Mat();
//
// // Capture loop
// while (true)
// {
//     var sp = Stopwatch.StartNew();
//     captureGraphics.CopyFromScreen(captureRectangle.Left, captureRectangle.Top, 0, 0, captureRectangle.Size);
//     
//     OpenCvSharp.Extensions.BitmapConverter.ToMat(captureBitmap, srcMat);
//     
//     Cv2.CvtColor(srcMat, betterSrcMat, ColorConversionCodes.RGB2GRAY);
//     Cv2.MatchTemplate(betterSrcMat, templateMat, matchResult, TemplateMatchModes.CCoeffNormed);
//     Cv2.MinMaxLoc(matchResult, out _, out var maxval, out _, out var maxloc);
//     sp.Stop();
//     
//     Console.SetCursorPosition(0, 0);
//     Console.WriteLine("Detected: {0}\nX: {1}\nY: {2}\nTime: {3}ms\nConfidence: {4}", maxval > 0.8 ? "Yes" : "No", maxloc.X, maxloc.Y, sp.ElapsedMilliseconds, maxval);
// }

// Cv2.Rectangle(srcMat, new Rect(maxloc.X, maxloc.Y, templateMat.Width, templateMat.Height), new Scalar(255, 0, 255), 2);
// srcMat.SaveImage("PlsRec.png");