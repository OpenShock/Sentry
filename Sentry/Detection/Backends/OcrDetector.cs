using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenShock.Sentry.Models;
using TesseractOCR;
using TesseractOCR.Enums;

namespace OpenShock.Sentry.Detection.Backends;

/// <summary>
/// OCR-based detector using Tesseract. Extracts text from the region and matches
/// against a regex pattern. Useful for detecting text events like "YOU DIED",
/// kill feeds, score displays, etc.
/// </summary>
public sealed class OcrDetector : IDetector
{
    private const string TessDataFastBaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";

    private readonly ILogger<OcrDetector> _logger;
    private readonly string _tessDataDir;
    private Engine? _engine;
    private Regex? _pattern;
    private bool _invertMatch;

    // Reusable buffer to avoid per-frame byte[] allocations from ImEncode
    private readonly MemoryStream _encodeBuffer = new();

    public string Name { get; private set; } = nameof(OcrDetector);

    public OcrDetector(ILogger<OcrDetector> logger, string tessDataDir)
    {
        _logger = logger;
        _tessDataDir = tessDataDir;
    }

    public async Task Initialize(DetectorConfig config, string profileBaseDir)
    {
        Name = config.Name;
        _invertMatch = config.InvertMatch;

        var settings = config.Settings.Deserialize<OcrSettings>(DetectorJsonOptions.Default)
            ?? throw new InvalidOperationException($"Detector '{Name}' missing Ocr settings");

        var language = Language.English;
        if (!string.IsNullOrEmpty(settings.Language)
            && !Enum.TryParse(settings.Language, ignoreCase: true, out language))
            throw new InvalidOperationException($"Detector '{Name}': unknown language '{settings.Language}'");

        if (string.IsNullOrEmpty(settings.Pattern))
            throw new InvalidOperationException($"Detector '{Name}' missing required setting 'pattern'");

        _pattern = new Regex(settings.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var engineMode = EngineMode.Default;
        if (!string.IsNullOrEmpty(settings.EngineMode)
            && !Enum.TryParse(settings.EngineMode, ignoreCase: true, out engineMode))
            throw new InvalidOperationException($"Detector '{Name}': unknown engine mode '{settings.EngineMode}'");

        // Ensure tessdata directory and trained data exist
        Directory.CreateDirectory(_tessDataDir);
        var langCode = GetTesseractLanguageCode(language);
        var trainedDataPath = Path.Combine(_tessDataDir, $"{langCode}.traineddata");
        if (!File.Exists(trainedDataPath))
        {
            _logger.LogInformation("Downloading Tesseract trained data for '{Language}'...", langCode);
            await DownloadTrainedDataAsync(langCode, trainedDataPath);
        }

        _engine = new Engine(_tessDataDir, language, engineMode,
            initialOptions: new Dictionary<string, object>
            {
                ["user_defined_dpi"] = 96,
                ["debug_file"] = "NUL"
            });

        _logger.LogInformation(
            "Initialized OCR detector '{Name}' with pattern '{Pattern}', language {Language}",
            Name, settings.Pattern, language);
    }

    public DetectionResult Detect(Mat regionFrame)
    {
        if (_engine is null || _pattern is null)
            return DetectionResult.NoMatch;

        // Skip tiny regions that cause Tesseract/Leptonica bounding box errors
        if (regionFrame.Empty() || regionFrame.Width < 10 || regionFrame.Height < 10)
            return DetectionResult.NoMatch;

        // Encode as BMP (no compression — much faster than PNG) into reusable buffer
        Cv2.ImEncode(".bmp", regionFrame, out var imageBytes);
        _encodeBuffer.SetLength(0);
        _encodeBuffer.Write(imageBytes);
        _encodeBuffer.Position = 0;

        using var img = TesseractOCR.Pix.Image.LoadFromMemory(_encodeBuffer);
        using var page = _engine.Process(img, PageSegMode.Auto);

        var text = page.Text?.Trim() ?? string.Empty;
        var confidence = page.MeanConfidence;
        var matched = _pattern.IsMatch(text);
        var triggered = _invertMatch ? !matched : matched;

        return new DetectionResult
        {
            Triggered = triggered,
            Confidence = confidence,
            Text = text
        };
    }

    private static async Task DownloadTrainedDataAsync(string langCode, string destPath)
    {
        var url = $"{TessDataFastBaseUrl}{langCode}.traineddata";
        using var http = new HttpClient();
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await response.Content.CopyToAsync(fs);
    }

    private static string GetTesseractLanguageCode(Language language)
    {
        return language switch
        {
            Language.English => "eng",
            Language.French => "fra",
            Language.German => "deu",
            Language.SpanishCastilian => "spa",
            Language.Italian => "ita",
            Language.Portuguese => "por",
            Language.Dutch => "nld",
            Language.Russian => "rus",
            Language.Japanese => "jpn",
            Language.ChineseSimplified => "chi_sim",
            Language.ChineseTraditional => "chi_tra",
            Language.Korean => "kor",
            Language.Arabic => "ara",
            Language.Polish => "pol",
            Language.Turkish => "tur",
            Language.Swedish => "swe",
            Language.Norwegian => "nor",
            Language.Danish => "dan",
            Language.Finnish => "fin",
            Language.Czech => "ces",
            Language.Hungarian => "hun",
            Language.Romanian => "ron",
            Language.Ukrainian => "ukr",
            Language.Vietnamese => "vie",
            Language.Thai => "tha",
            Language.Hindi => "hin",
            Language.Hebrew => "heb",
            Language.GreekModern => "ell",
            Language.Bengali => "ben",
            _ => language.ToString().ToLowerInvariant()
        };
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _encodeBuffer.Dispose();
    }
}
