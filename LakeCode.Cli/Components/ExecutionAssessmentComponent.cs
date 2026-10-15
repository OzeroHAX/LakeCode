using System.Globalization;
using LakeCode.Core.ExecutionApproval;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class ExecutionAssessmentComponent : PanelControl
{
    private readonly MarkupControl _summary = new(new List<string>()) { Wrap = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CollapsiblePanel _details = new()
    {
        Title = "Оценки Jev", IsExpanded = false, Collapsible = true,
        HorizontalAlignment = HorizontalAlignment.Stretch, MaxContentHeight = int.MaxValue
    };
    private readonly MarkupControl _values = new(new List<string>()) { Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch };

    public ExecutionAssessmentComponent()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _details.AddControl(_values);
        AddControl(_summary);
        AddControl(_details);
        Visible = false;
    }

    public void Update(ExecutionAssessment? assessment)
    {
        Visible = assessment is not null;
        if (assessment is null) return;
        _summary.Text = MarkupParser.Escape(assessment.Summary);
        _details.Visible = assessment.Criteria.Length > 0;
        if (!_details.Visible) return;
        // Old history keeps its original policy; never reinterpret a three-way answer as a binary one.
        if (assessment.PolicyVersion != "jev-plan-v2-binary")
        {
            _values.Text = MarkupParser.Escape($"Историческая оценка · {assessment.PolicyVersion}\n{assessment.Reason}\nМодель: {assessment.Model}");
            return;
        }
        if (assessment.AllowThreshold is not { } threshold || assessment.DenyThreshold is not { } denyThreshold)
        {
            _values.Text = "Сохранённая оценка неполна: отсутствуют пороги.";
            return;
        }
        var rows = assessment.Criteria.Select(c =>
            $"{Label(c.Id)}: {(c.Safe >= threshold ? "нет риска" : "есть риск")}\n  P(нет риска): {Percent(c.Safe)} · P(есть риск): {Percent(c.Risk)}");
        _values.Text = MarkupParser.Escape(string.Join("\n", rows) +
            $"\n\nРешение по порогу: нет риска при P(нет риска) ≥ {Percent(threshold)}, иначе есть риск.\n" +
            $"Порог запрета: {Percent(denyThreshold)} · Модель: {assessment.Model}\nПолитика: {assessment.PolicyVersion}");
    }

    private static string Percent(double value) => (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
    private static string Label(string id) => id switch
    {
        "description" => "Несоответствие описания", "local" => "Локальные изменения",
        "external" => "Внешние изменения", "financial" => "Финансовые эффекты",
        "disclosure" => "Передача непубличных данных", "understanding" => "Недостаточная понятность",
        _ => id
    };
}
