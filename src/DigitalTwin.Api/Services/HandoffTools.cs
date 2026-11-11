using System.ComponentModel;
using System.Text.Json;

namespace DigitalTwin.Api;

/// <summary>Agent only proposes an export; the user must approve it in UI.</summary>
public sealed class HandoffTools(ApprovalQueue queue, ToolAuthorizationService authorization)
{
    [Description("Submit a completed technical handoff for human review ONLY when the user explicitly asks for a gated/reviewable proposal. Does not export, write project files, publish tickets, or grant access. Do not call for normal answers.")]
    public string ProposeHandoff(
        [Description("Required short, human-readable title of the handoff, maximum 120 characters.")] string title,
        [Description("Required Markdown report (1–35000 characters) with purpose, stack, architecture, components, setup, risks, and source evidence.")] string content)
    {
        using var toolTelemetry = DeveloperTwinTelemetry.BeginTool("ProposeHandoff");
        try
        {
        authorization.AuthorizeHandoff();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || string.IsNullOrWhiteSpace(content) || content.Length > 35000)
            return "Please provide a title below 120 characters and report between 1 and 35,000 characters.";
        title = SensitiveDataPolicy.Redact(title);
        content = SensitiveDataPolicy.Redact(content);
        var action = queue.Propose("handoff-export", title, JsonSerializer.Serialize(new HandoffDraft(title, content)));
        return $"Handoff proposal {action.Id} awaits your approval in the Approvals view.";
        }
        catch { toolTelemetry.Fail(); throw; }
    }
}
