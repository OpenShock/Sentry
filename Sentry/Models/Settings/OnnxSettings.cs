namespace OpenShock.Sentry.Models;

public sealed class OnnxSettings
{
    public string ModelPath { get; set; } = "";
    public string Label { get; set; } = "";
    public float Threshold { get; set; } = 0.9f;
}