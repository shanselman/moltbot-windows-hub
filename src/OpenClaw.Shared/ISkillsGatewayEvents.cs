namespace OpenClaw.Shared;

/// <summary>
/// Optional extension event surface for consumers that refresh typed skills state.
/// Keeping this separate avoids source-breaking existing operator client implementers.
/// </summary>
public interface ISkillsGatewayEvents
{
    event EventHandler? SkillsChanged;
}
