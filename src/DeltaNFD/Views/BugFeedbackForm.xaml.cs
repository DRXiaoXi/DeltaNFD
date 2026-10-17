using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class BugFeedbackForm : UserControl
{
    private bool _validationRequested;
    private readonly Dictionary<BugFeedbackField, (Control Input, TextBlock Error)> _fields;

    public BugFeedbackForm()
    {
        InitializeComponent();
        TitleInput.MaxLength = BugFeedbackService.MaximumTitleCharacters;
        DescriptionInput.MaxLength = BugFeedbackService.MaximumDescriptionCharacters;
        AreaInput.ItemsSource = BugFeedbackService.Areas;
        AreaInput.SelectedIndex = BugFeedbackService.Areas.Length - 1;
        _fields = new()
        {
            [BugFeedbackField.Title] = (TitleInput, TitleError),
            [BugFeedbackField.Area] = (AreaInput, AreaError),
            [BugFeedbackField.Description] = (DescriptionInput, DescriptionError),
            [BugFeedbackField.Contact] = (ContactInput, ContactError),
        };
        TitleInput.TextChanged += (_, _) => RefreshValidation();
        DescriptionInput.TextChanged += (_, _) => RefreshValidation();
        ContactInput.TextChanged += (_, _) => RefreshValidation();
        AreaInput.SelectionChanged += (_, _) => RefreshValidation();
        IncludeLogsInput.Checked += (_, _) => RefreshValidation();
        IncludeLogsInput.Unchecked += (_, _) => RefreshValidation();
        RefreshValidation();
    }

    public BugFeedbackInput ReadInput() => new(TitleInput.Text, AreaInput.SelectedItem as string ?? "",
        DescriptionInput.Text, ContactInput.Text, IncludeLogsInput.IsChecked == true);

    public bool ValidateAndFocus()
    {
        _validationRequested = true;
        var issues = RefreshValidation();
        if (issues.Count == 0) return true;
        var control = _fields[issues[0].Field].Input;
        control.Focus(FocusState.Programmatic);
        control.StartBringIntoView();
        return false;
    }

    public void ShowPreparationError(string message)
    {
        ValidationSummary.Title = "反馈预览未生成";
        ValidationSummary.Message = message;
        ValidationSummary.IsOpen = true;
    }

    private IReadOnlyList<BugFeedbackValidationIssue> RefreshValidation()
    {
        DescriptionCounter.Text = BugFeedbackService.DescriptionProgress(DescriptionInput.Text);
        if (!_validationRequested) return [];
        var issues = BugFeedbackService.ValidateFields(ReadInput());
        foreach (var (field, controls) in _fields)
        {
            var message = issues.FirstOrDefault(i => i.Field == field)?.Message ?? "";
            controls.Error.Text = message;
            controls.Error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(controls.Input, message);
        }
        ValidationSummary.Title = "无法生成预览，请修改以下内容";
        ValidationSummary.Message = string.Join("\n", issues.Select(i => i.Message));
        ValidationSummary.IsOpen = issues.Count > 0;
        return issues;
    }
}
