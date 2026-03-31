using OpenShock.Desktop.ModuleBase.Models;

namespace OpenShock.Sentry.Models;

public sealed class ActionMapping
{
    public GameEventType EventType { get; set; }
    public ControlType ControlType { get; set; } = ControlType.Shock;
    public byte Intensity { get; set; } = 30;
    public ushort Duration { get; set; } = 500;
    public int CooldownMs { get; set; } = 1000;
}
