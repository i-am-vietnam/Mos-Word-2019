using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MosWord2019.Core.Models;
namespace MosWord2019
{
    public sealed class ResultForm : Form
    {
        public ResultForm(ExamResult result, IReadOnlyList<ProjectPackage> packages, string language)
        {
            Func<string, string, string> t = (en, vi) => language == "vi" ? vi : en;
            Text = t("Exam result", "Kết quả bài thi"); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; BackColor = Color.White;
            ClientSize = new Size(780, 600); MinimumSize = new Size(560, 400);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(new Label { Name = "TotalScore", Text = result.Total + " / 1000", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 32, FontStyle.Bold) });
            layout.Controls.Add(new Label { Name = "Verdict", Text = result.Passed ? t("Pass", "Đạt") : t("Needs more practice", "Cần Luyện Tập thêm"),
                Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 14, FontStyle.Bold), ForeColor = result.Passed ? Color.DarkGreen : Color.DarkOrange });
            var scores = new ListView { Name = "ProjectScores", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            scores.Columns.Add(t("Project", "Project"), 260); scores.Columns.Add(t("Score", "Điểm"), 180);
            foreach (var project in result.PerProject) scores.Items.Add(new ListViewItem(new[] { project.ProjectId.Substring("Word2019_".Length), project.Points + " / " + project.MaximumPoints }));
            layout.Controls.Add(scores);
            layout.Controls.Add(new Label { Text = t("Incorrect tasks:", "Các task sai:"), Dock = DockStyle.Fill });
            var lines = result.WrongTasks.Select(w =>
            {
                var package = packages.Single(p => p.Meta.ProjectId == "Word2019_" + w.ProjectCode);
                string instruction = package.Lang[w.InstructionKey].Replace("\r", " ").Replace("\n", " ");
                return w.ProjectCode + "-" + w.TaskId + ": " + instruction;
            });
            layout.Controls.Add(new TextBox { Name = "WrongTasks", Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 13), Dock = DockStyle.Fill, Text = string.Join(Environment.NewLine, lines) });
            var close = new Button { Text = t("Close", "Đóng"), AutoSize = true, DialogResult = DialogResult.OK }; layout.Controls.Add(close);
            Shown += (s, e) => { var area = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height)); CenterToScreen(); };
            Controls.Add(layout); AcceptButton = close; CancelButton = close;
        }
    }
}
