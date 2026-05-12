using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.ChatControl;

/// <summary>
/// Selects the appropriate DataTemplate based on the message role.
/// </summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? SystemTemplate { get; set; }
    public DataTemplate? StatusTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is ChatMessage msg)
        {
            return msg.Role switch
            {
                MessageRole.User => UserTemplate,
                MessageRole.Assistant => AssistantTemplate,
                MessageRole.System => SystemTemplate,
                MessageRole.Status => StatusTemplate ?? SystemTemplate,
                _ => AssistantTemplate
            };
        }
        return base.SelectTemplateCore(item, container);
    }
}
