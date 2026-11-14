using System.IO;
using System.Text.Json;
using System.Windows;
using Baymax.Contracts;

namespace Baymax.Desktop;

public partial class PlanEditorWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly TaskPlanResponse _originalPlan;

    public PlanEditorWindow(TaskPlanResponse plan)
    {
        InitializeComponent();
        _originalPlan = plan;
        var document = new EditablePlanDocument(
            plan.Summary,
            plan.Actions.Select(action => new EditablePlanAction(action.Name, action.Arguments)).ToArray());
        PlanJsonTextBox.Text = JsonSerializer.Serialize(document, JsonOptions);
    }

    public TaskPlanResponse? EditedPlan { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PlanJsonTextBox.Text.Length > 6 * 1024 * 1024)
            {
                throw new InvalidDataException("The edited plan must be 6 MiB or smaller.");
            }

            using var json = JsonDocument.Parse(PlanJsonTextBox.Text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("summary", out var summaryElement)
                || summaryElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("actions", out var actionsElement)
                || actionsElement.ValueKind != JsonValueKind.Array
                || root.EnumerateObject().Any(property => property.Name is not ("summary" or "actions")))
            {
                throw new InvalidDataException("Plan JSON must contain only a string 'summary' and an 'actions' array.");
            }

            var summary = summaryElement.GetString()!;
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > 500)
            {
                throw new InvalidDataException("The summary must contain 1 to 500 non-whitespace characters.");
            }

            if (actionsElement.GetArrayLength() is < 1 or > 20)
            {
                throw new InvalidDataException("The actions array must contain 1 to 20 actions.");
            }

            var actions = new List<PlannedAction>(actionsElement.GetArrayLength());
            var ordinal = 1;
            foreach (var actionElement in actionsElement.EnumerateArray())
            {
                if (actionElement.ValueKind != JsonValueKind.Object
                    || !actionElement.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || !actionElement.TryGetProperty("arguments", out var argumentsElement)
                    || argumentsElement.ValueKind != JsonValueKind.Object
                    || actionElement.EnumerateObject().Any(property => property.Name is not ("name" or "arguments")))
                {
                    throw new InvalidDataException(
                        $"Action {ordinal} must contain only a string 'name' and an object 'arguments'.");
                }

                actions.Add(new PlannedAction(
                    Guid.NewGuid(),
                    ordinal++,
                    nameElement.GetString()!,
                    argumentsElement.Clone()));
            }

            EditedPlan = _originalPlan with
            {
                Summary = summary,
                NeedsClarification = false,
                ClarifyingQuestion = string.Empty,
                Actions = actions
            };
            DialogResult = true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
        {
            EditorStatusText.Text = exception.Message;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
