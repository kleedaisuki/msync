using PrettyPrompt.Completion;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;

namespace Msync.Cli;

internal sealed class CaretCompletionItem(string replacementText, FormattedString displayText = default,
    string? filterText = null, CompletionItem.GetExtendedDescriptionHandler? getExtendedDescription = null)
    : CompletionItem(replacementText, displayText, filterText, getExtendedDescription)
{
    public override int GetCompletionItemPriority(string text, int caret, TextSpan spanToBeReplaced) =>
        base.GetCompletionItemPriority(text, caret, TextSpan.FromBounds(spanToBeReplaced.Start, Math.Min(caret, spanToBeReplaced.End)));
}
