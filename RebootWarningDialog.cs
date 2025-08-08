using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelfCareService
{
    public partial class RebootWarningDialog : Form
    {
        public SkipDuration? SelectedSkipDuration { get; private set; }
        private readonly SkipDuration[] _availableOptions;

        public RebootWarningDialog(TimeSpan uptime, SkipDuration[] availableOptions, int alertCount)
        {
            _availableOptions = availableOptions;
            InitializeComponent(uptime, alertCount);
        }

        private void InitializeComponent(TimeSpan uptime, int alertCount)
        {
            // Form properties
            this.Text = "SelfCare - Reboot Reminder";
            this.Size = new Size(500, 320);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.TopMost = true;
            this.ShowInTaskbar = true;
            this.Icon = SystemIcons.Warning;

            // Main panel
            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                BackColor = Color.White
            };

            // Warning icon
            var iconLabel = new Label
            {
                Image = SystemIcons.Warning.ToBitmap(),
                Size = new Size(32, 32),
                Location = new Point(20, 20)
            };

            // Title label
            var titleLabel = new Label
            {
                Text = "System Uptime Warning",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Location = new Point(70, 20),
                Size = new Size(400, 30),
                ForeColor = Color.DarkRed
            };

            // Message label
            string messageText;
            if (uptime.Days > 0)
            {
                messageText = $"Your system has been running for {uptime.Days} days and {uptime.Hours} hours.\n\n" +
                             "Regular reboots help maintain system stability and apply important updates.\n\n" +
                             $"This is alert #{alertCount + 1}. Available skip options are being reduced.";
            }
            else
            {
                messageText = $"Your system has been running for {uptime.Hours} hours and {uptime.Minutes} minutes.\n\n" +
                             "Regular reboots help maintain system stability and apply important updates.\n\n" +
                             $"This is alert #{alertCount + 1}. Available skip options are being reduced.";
            }

            var messageLabel = new Label
            {
                Text = messageText,
                Font = new Font("Segoe UI", 10),
                Location = new Point(20, 70),
                Size = new Size(440, 120),
                ForeColor = Color.Black
            };

            // Question label
            var questionLabel = new Label
            {
                Text = "What would you like to do?",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                Location = new Point(20, 200),
                Size = new Size(440, 25),
                ForeColor = Color.Black
            };

            // Buttons panel
            var buttonPanel = new Panel
            {
                Location = new Point(20, 230),
                Size = new Size(440, 40),
                BackColor = Color.White
            };

            // Reboot button
            var rebootButton = new Button
            {
                Text = "🔄 Reboot Now",
                Size = new Size(100, 35),
                Location = new Point(0, 0),
                BackColor = Color.Tomato,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            rebootButton.FlatAppearance.BorderSize = 0;
            rebootButton.Click += (s, e) => { this.DialogResult = DialogResult.Yes; this.Close(); };

            buttonPanel.Controls.Add(rebootButton);

            // Skip buttons
            int xPos = 110;
            for (int i = 0; i < _availableOptions.Length; i++)
            {
                var option = _availableOptions[i];
                var skipButton = new Button
                {
                    Text = $"⏸ Skip {option.ToDisplayString()}",
                    Size = new Size(95, 35),
                    Location = new Point(xPos, 0),
                    BackColor = Color.LightBlue,
                    ForeColor = Color.DarkBlue,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8),
                    UseVisualStyleBackColor = false,
                    Tag = option
                };
                skipButton.FlatAppearance.BorderSize = 1;
                skipButton.FlatAppearance.BorderColor = Color.DarkBlue;
                skipButton.Click += SkipButton_Click;

                buttonPanel.Controls.Add(skipButton);
                xPos += 100;
            }

            // Add all controls to main panel
            mainPanel.Controls.Add(iconLabel);
            mainPanel.Controls.Add(titleLabel);
            mainPanel.Controls.Add(messageLabel);
            mainPanel.Controls.Add(questionLabel);
            mainPanel.Controls.Add(buttonPanel);

            // Add main panel to form
            this.Controls.Add(mainPanel);

            // Set dialog timeout (5 minutes)
            var timer = new Timer();
            timer.Interval = 5 * 60 * 1000; // 5 minutes
            timer.Tick += (s, e) => {
                timer.Stop();
                // Default to shortest skip if no action taken
                SelectedSkipDuration = SkipDuration.Minutes10;
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };
            timer.Start();
        }

        private void SkipButton_Click(object sender, EventArgs e)
        {
            if (sender is Button button && button.Tag is SkipDuration duration)
            {
                SelectedSkipDuration = duration;
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // If form is closed without selection, default to shortest skip
            if (this.DialogResult == DialogResult.None)
            {
                SelectedSkipDuration = SkipDuration.Minutes10;
                this.DialogResult = DialogResult.Cancel;
            }
            base.OnFormClosing(e);
        }
    }
}
