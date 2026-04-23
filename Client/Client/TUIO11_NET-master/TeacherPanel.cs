using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace TUIO
{
    /// <summary>
    /// Teacher Management Panel for CRUD operations on students.
    /// Allows teachers to add, edit, delete, and view student records,
    /// view attendance reports, and analyze gaze tracking data.
    /// </summary>
    public class TeacherPanel : Form
    {
        private DatabaseManager db;
        private TabControl tabControl;
        
        // Students Tab
        private DataGridView studentsGrid;
        private TextBox txtName;
        private TextBox txtBluetooth;
        private ComboBox cmbRole;
        private Button btnAdd;
        private Button btnUpdate;
        private Button btnDelete;
        private Button btnClear;
        private int selectedUserId = -1;

        // Attendance Tab
        private DataGridView attendanceGrid;
        private ComboBox cmbFilterUser;
        private DateTimePicker dtpFrom;
        private DateTimePicker dtpTo;
        private Button btnFilterAttendance;
        private Label lblAttendanceStats;

        // Statistics Tab
        private ListView statsList;
        private Button btnRefreshStats;
        private Button btnExportData;

        public TeacherPanel(DatabaseManager database)
        {
            db = database;
            InitializeComponent();
            RefreshStudentsList();
            RefreshStats();
        }

        private void InitializeComponent()
        {
            this.Text = "Teacher Management Panel";
            this.Size = new Size(900, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(30, 30, 40);
            this.ForeColor = Color.White;

            // Tab Control
            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill;
            tabControl.BackColor = Color.FromArgb(30, 30, 40);
            tabControl.ForeColor = Color.Black;

            // Create tabs
            tabControl.TabPages.Add(CreateStudentsTab());
            tabControl.TabPages.Add(CreateAttendanceTab());
            tabControl.TabPages.Add(CreateStatisticsTab());

            this.Controls.Add(tabControl);
        }

        #region Students Tab

        private TabPage CreateStudentsTab()
        {
            var page = new TabPage("Students Management");
            page.BackColor = Color.FromArgb(30, 30, 40);

            // Students Grid
            studentsGrid = new DataGridView();
            studentsGrid.Location = new Point(10, 10);
            studentsGrid.Size = new Size(550, 450);
            studentsGrid.BackgroundColor = Color.FromArgb(40, 40, 50);
            studentsGrid.ForeColor = Color.Black;
            studentsGrid.GridColor = Color.Gray;
            studentsGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(60, 60, 80);
            studentsGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            studentsGrid.EnableHeadersVisualStyles = false;
            studentsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            studentsGrid.MultiSelect = false;
            studentsGrid.ReadOnly = true;
            studentsGrid.RowHeadersVisible = false;
            studentsGrid.AllowUserToAddRows = false;
            studentsGrid.AllowUserToDeleteRows = false;
            studentsGrid.CellClick += StudentsGrid_CellClick;

            // Define columns
            studentsGrid.Columns.Add("Id", "ID");
            studentsGrid.Columns.Add("Name", "Name");
            studentsGrid.Columns.Add("Role", "Role");
            studentsGrid.Columns.Add("Bluetooth", "Bluetooth Address");
            studentsGrid.Columns.Add("Created", "Created");
            studentsGrid.Columns[0].Width = 50;
            studentsGrid.Columns[1].Width = 150;
            studentsGrid.Columns[2].Width = 80;
            studentsGrid.Columns[3].Width = 150;
            studentsGrid.Columns[4].Width = 120;

            // Input Panel
            var inputPanel = new Panel();
            inputPanel.Location = new Point(580, 10);
            inputPanel.Size = new Size(280, 450);
            inputPanel.BackColor = Color.FromArgb(40, 40, 50);

            int y = 20;
            
            var lblTitle = new Label();
            lblTitle.Text = "Student Details";
            lblTitle.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            lblTitle.ForeColor = Color.Gold;
            lblTitle.Location = new Point(10, y);
            lblTitle.Size = new Size(260, 25);
            inputPanel.Controls.Add(lblTitle);
            y += 40;

            var lblName = new Label();
            lblName.Text = "Name:";
            lblName.Location = new Point(10, y);
            lblName.Size = new Size(80, 20);
            inputPanel.Controls.Add(lblName);

            txtName = new TextBox();
            txtName.Location = new Point(90, y);
            txtName.Size = new Size(180, 25);
            inputPanel.Controls.Add(txtName);
            y += 35;

            var lblRole = new Label();
            lblRole.Text = "Role:";
            lblRole.Location = new Point(10, y);
            lblRole.Size = new Size(80, 20);
            inputPanel.Controls.Add(lblRole);

            cmbRole = new ComboBox();
            cmbRole.Location = new Point(90, y);
            cmbRole.Size = new Size(180, 25);
            cmbRole.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRole.Items.AddRange(new string[] { "Student", "Teacher" });
            cmbRole.SelectedIndex = 0;
            inputPanel.Controls.Add(cmbRole);
            y += 35;

            var lblBluetooth = new Label();
            lblBluetooth.Text = "Bluetooth:";
            lblBluetooth.Location = new Point(10, y);
            lblBluetooth.Size = new Size(80, 20);
            inputPanel.Controls.Add(lblBluetooth);

            txtBluetooth = new TextBox();
            txtBluetooth.Location = new Point(90, y);
            txtBluetooth.Size = new Size(180, 25);
            inputPanel.Controls.Add(txtBluetooth);
            y += 50;

            // Buttons
            btnAdd = new Button();
            btnAdd.Text = "Add User";
            btnAdd.Location = new Point(10, y);
            btnAdd.Size = new Size(120, 35);
            btnAdd.BackColor = Color.FromArgb(0, 150, 0);
            btnAdd.ForeColor = Color.White;
            btnAdd.FlatStyle = FlatStyle.Flat;
            btnAdd.Click += BtnAdd_Click;
            inputPanel.Controls.Add(btnAdd);

            btnUpdate = new Button();
            btnUpdate.Text = "Update";
            btnUpdate.Location = new Point(140, y);
            btnUpdate.Size = new Size(120, 35);
            btnUpdate.BackColor = Color.FromArgb(0, 100, 150);
            btnUpdate.ForeColor = Color.White;
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.Enabled = false;
            btnUpdate.Click += BtnUpdate_Click;
            inputPanel.Controls.Add(btnUpdate);
            y += 45;

            btnDelete = new Button();
            btnDelete.Text = "Delete";
            btnDelete.Location = new Point(10, y);
            btnDelete.Size = new Size(120, 35);
            btnDelete.BackColor = Color.FromArgb(150, 0, 0);
            btnDelete.ForeColor = Color.White;
            btnDelete.FlatStyle = FlatStyle.Flat;
            btnDelete.Enabled = false;
            btnDelete.Click += BtnDelete_Click;
            inputPanel.Controls.Add(btnDelete);

            btnClear = new Button();
            btnClear.Text = "Clear Form";
            btnClear.Location = new Point(140, y);
            btnClear.Size = new Size(120, 35);
            btnClear.BackColor = Color.FromArgb(100, 100, 100);
            btnClear.ForeColor = Color.White;
            btnClear.FlatStyle = FlatStyle.Flat;
            btnClear.Click += BtnClear_Click;
            inputPanel.Controls.Add(btnClear);
            y += 60;

            var lblHint = new Label();
            lblHint.Text = "Select a row to edit or delete.\nLeave Bluetooth blank if not available.";
            lblHint.Location = new Point(10, y);
            lblHint.Size = new Size(260, 50);
            lblHint.ForeColor = Color.Gray;
            inputPanel.Controls.Add(lblHint);

            page.Controls.Add(studentsGrid);
            page.Controls.Add(inputPanel);

            return page;
        }

        private void StudentsGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0)
            {
                var row = studentsGrid.Rows[e.RowIndex];
                selectedUserId = (int)row.Cells["Id"].Value;
                txtName.Text = row.Cells["Name"].Value.ToString();
                cmbRole.SelectedItem = row.Cells["Role"].Value.ToString();
                txtBluetooth.Text = row.Cells["Bluetooth"].Value?.ToString() ?? "";
                
                btnUpdate.Enabled = true;
                btnDelete.Enabled = true;
            }
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Please enter a name.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var user = db.CreateUser(
                    txtName.Text.Trim(),
                    cmbRole.SelectedItem.ToString(),
                    string.IsNullOrWhiteSpace(txtBluetooth.Text) ? null : txtBluetooth.Text.Trim()
                );

                MessageBox.Show($"User created successfully!\nID: {user.Id}\nName: {user.Name}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ClearForm();
                RefreshStudentsList();
                RefreshAttendanceFilter();
                RefreshStats();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnUpdate_Click(object sender, EventArgs e)
        {
            if (selectedUserId < 0) return;

            var user = db.GetUserById(selectedUserId);
            if (user != null)
            {
                user.Name = txtName.Text.Trim();
                user.Role = cmbRole.SelectedItem.ToString();
                user.BluetoothAddress = string.IsNullOrWhiteSpace(txtBluetooth.Text) ? null : txtBluetooth.Text.Trim();
                
                db.UpdateUser(user);
                MessageBox.Show("User updated successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ClearForm();
                RefreshStudentsList();
                RefreshStats();
            }
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            if (selectedUserId < 0) return;

            var result = MessageBox.Show(
                "Are you sure you want to delete this user?\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                db.DeleteUser(selectedUserId);
                MessageBox.Show("User deleted successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ClearForm();
                RefreshStudentsList();
                RefreshAttendanceFilter();
                RefreshStats();
            }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            ClearForm();
        }

        private void ClearForm()
        {
            selectedUserId = -1;
            txtName.Text = "";
            txtBluetooth.Text = "";
            cmbRole.SelectedIndex = 0;
            btnUpdate.Enabled = false;
            btnDelete.Enabled = false;
            studentsGrid.ClearSelection();
        }

        private void RefreshStudentsList()
        {
            studentsGrid.Rows.Clear();
            var users = db.GetAllUsers();
            foreach (var user in users)
            {
                studentsGrid.Rows.Add(
                    user.Id,
                    user.Name,
                    user.Role,
                    user.BluetoothAddress ?? "-",
                    user.CreatedAt.ToString("yyyy-MM-dd HH:mm")
                );
            }
        }

        #endregion

        #region Attendance Tab

        private TabPage CreateAttendanceTab()
        {
            var page = new TabPage("Attendance Reports");
            page.BackColor = Color.FromArgb(30, 30, 40);

            // Filter Panel
            var filterPanel = new Panel();
            filterPanel.Location = new Point(10, 10);
            filterPanel.Size = new Size(860, 60);
            filterPanel.BackColor = Color.FromArgb(40, 40, 50);

            int x = 10;

            var lblUser = new Label();
            lblUser.Text = "User:";
            lblUser.Location = new Point(x, 20);
            lblUser.Size = new Size(40, 20);
            filterPanel.Controls.Add(lblUser);
            x += 45;

            cmbFilterUser = new ComboBox();
            cmbFilterUser.Location = new Point(x, 18);
            cmbFilterUser.Size = new Size(150, 25);
            cmbFilterUser.DropDownStyle = ComboBoxStyle.DropDownList;
            filterPanel.Controls.Add(cmbFilterUser);
            x += 170;

            var lblFrom = new Label();
            lblFrom.Text = "From:";
            lblFrom.Location = new Point(x, 20);
            lblFrom.Size = new Size(40, 20);
            filterPanel.Controls.Add(lblFrom);
            x += 45;

            dtpFrom = new DateTimePicker();
            dtpFrom.Location = new Point(x, 18);
            dtpFrom.Size = new Size(120, 25);
            dtpFrom.Format = DateTimePickerFormat.Short;
            dtpFrom.Value = DateTime.Today.AddDays(-7);
            filterPanel.Controls.Add(dtpFrom);
            x += 130;

            var lblTo = new Label();
            lblTo.Text = "To:";
            lblTo.Location = new Point(x, 20);
            lblTo.Size = new Size(30, 20);
            filterPanel.Controls.Add(lblTo);
            x += 35;

            dtpTo = new DateTimePicker();
            dtpTo.Location = new Point(x, 18);
            dtpTo.Size = new Size(120, 25);
            dtpTo.Format = DateTimePickerFormat.Short;
            dtpTo.Value = DateTime.Today;
            filterPanel.Controls.Add(dtpTo);
            x += 140;

            btnFilterAttendance = new Button();
            btnFilterAttendance.Text = "Filter";
            btnFilterAttendance.Location = new Point(x, 15);
            btnFilterAttendance.Size = new Size(100, 30);
            btnFilterAttendance.BackColor = Color.FromArgb(0, 100, 150);
            btnFilterAttendance.ForeColor = Color.White;
            btnFilterAttendance.FlatStyle = FlatStyle.Flat;
            btnFilterAttendance.Click += BtnFilterAttendance_Click;
            filterPanel.Controls.Add(btnFilterAttendance);

            page.Controls.Add(filterPanel);

            // Attendance Grid
            attendanceGrid = new DataGridView();
            attendanceGrid.Location = new Point(10, 80);
            attendanceGrid.Size = new Size(860, 400);
            attendanceGrid.BackgroundColor = Color.FromArgb(40, 40, 50);
            attendanceGrid.ForeColor = Color.Black;
            attendanceGrid.GridColor = Color.Gray;
            attendanceGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(60, 60, 80);
            attendanceGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            attendanceGrid.EnableHeadersVisualStyles = false;
            attendanceGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            attendanceGrid.ReadOnly = true;
            attendanceGrid.RowHeadersVisible = false;
            attendanceGrid.AllowUserToAddRows = false;

            attendanceGrid.Columns.Add("Id", "ID");
            attendanceGrid.Columns.Add("UserName", "User Name");
            attendanceGrid.Columns.Add("LoginTime", "Login");
            attendanceGrid.Columns.Add("LogoutTime", "Logout");
            attendanceGrid.Columns.Add("Duration", "Duration");
            attendanceGrid.Columns.Add("Scenes", "Scenes");
            attendanceGrid.Columns.Add("Challenges", "Challenges");

            attendanceGrid.Columns[0].Width = 50;
            attendanceGrid.Columns[1].Width = 150;
            attendanceGrid.Columns[2].Width = 130;
            attendanceGrid.Columns[3].Width = 130;
            attendanceGrid.Columns[4].Width = 80;
            attendanceGrid.Columns[5].Width = 70;
            attendanceGrid.Columns[6].Width = 80;

            page.Controls.Add(attendanceGrid);

            // Stats Label
            lblAttendanceStats = new Label();
            lblAttendanceStats.Location = new Point(10, 490);
            lblAttendanceStats.Size = new Size(860, 30);
            lblAttendanceStats.ForeColor = Color.Gold;
            lblAttendanceStats.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            page.Controls.Add(lblAttendanceStats);

            RefreshAttendanceFilter();
            BtnFilterAttendance_Click(null, null);

            return page;
        }

        private void RefreshAttendanceFilter()
        {
            cmbFilterUser.Items.Clear();
            cmbFilterUser.Items.Add("All Users");
            var users = db.GetAllUsers();
            foreach (var user in users)
            {
                cmbFilterUser.Items.Add($"{user.Name} (ID: {user.Id})");
            }
            cmbFilterUser.SelectedIndex = 0;
        }

        private void BtnFilterAttendance_Click(object sender, EventArgs e)
        {
            int? userId = null;
            if (cmbFilterUser.SelectedIndex > 0)
            {
                string selected = cmbFilterUser.SelectedItem.ToString();
                int idStart = selected.IndexOf("ID: ") + 4;
                int idEnd = selected.IndexOf(")", idStart);
                if (int.TryParse(selected.Substring(idStart, idEnd - idStart), out int uid))
                    userId = uid;
            }

            var records = db.GetAttendanceHistory(userId, dtpFrom.Value, dtpTo.Value.AddDays(1));
            
            attendanceGrid.Rows.Clear();
            TimeSpan totalDuration = TimeSpan.Zero;
            int totalScenes = 0;
            int totalChallenges = 0;

            foreach (var record in records)
            {
                string duration = "-";
                if (record.LogoutTime.HasValue)
                {
                    var dur = record.LogoutTime.Value - record.LoginTime;
                    duration = $"{dur.Hours}h {dur.Minutes}m";
                    totalDuration += dur;
                }

                attendanceGrid.Rows.Add(
                    record.Id,
                    record.UserName,
                    record.LoginTime.ToString("yyyy-MM-dd HH:mm"),
                    record.LogoutTime?.ToString("yyyy-MM-dd HH:mm") ?? "Active",
                    duration,
                    record.ScenesCompleted,
                    record.ChallengesCompleted
                );

                totalScenes += record.ScenesCompleted;
                totalChallenges += record.ChallengesCompleted;
            }

            lblAttendanceStats.Text = $"Total Sessions: {records.Count} | Total Duration: {totalDuration.Hours}h {totalDuration.Minutes}m | " +
                                      $"Total Scenes: {totalScenes} | Total Challenges: {totalChallenges}";
        }

        #endregion

        #region Statistics Tab

        private TabPage CreateStatisticsTab()
        {
            var page = new TabPage("Statistics & Export");
            page.BackColor = Color.FromArgb(30, 30, 40);

            // Stats ListView
            statsList = new ListView();
            statsList.Location = new Point(10, 10);
            statsList.Size = new Size(550, 480);
            statsList.View = View.Details;
            statsList.BackColor = Color.FromArgb(40, 40, 50);
            statsList.ForeColor = Color.White;
            statsList.Columns.Add("Metric", 250);
            statsList.Columns.Add("Value", 250);
            statsList.HeaderStyle = ColumnHeaderStyle.Nonclickable;

            page.Controls.Add(statsList);

            // Actions Panel
            var actionsPanel = new Panel();
            actionsPanel.Location = new Point(580, 10);
            actionsPanel.Size = new Size(280, 480);
            actionsPanel.BackColor = Color.FromArgb(40, 40, 50);

            var lblActions = new Label();
            lblActions.Text = "Actions";
            lblActions.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            lblActions.ForeColor = Color.Gold;
            lblActions.Location = new Point(10, 20);
            lblActions.Size = new Size(260, 25);
            actionsPanel.Controls.Add(lblActions);

            btnRefreshStats = new Button();
            btnRefreshStats.Text = "Refresh Statistics";
            btnRefreshStats.Location = new Point(10, 60);
            btnRefreshStats.Size = new Size(260, 40);
            btnRefreshStats.BackColor = Color.FromArgb(0, 100, 150);
            btnRefreshStats.ForeColor = Color.White;
            btnRefreshStats.FlatStyle = FlatStyle.Flat;
            btnRefreshStats.Click += (s, e) => RefreshStats();
            actionsPanel.Controls.Add(btnRefreshStats);

            btnExportData = new Button();
            btnExportData.Text = "Export to CSV";
            btnExportData.Location = new Point(10, 110);
            btnExportData.Size = new Size(260, 40);
            btnExportData.BackColor = Color.FromArgb(0, 150, 0);
            btnExportData.ForeColor = Color.White;
            btnExportData.FlatStyle = FlatStyle.Flat;
            btnExportData.Click += BtnExportData_Click;
            actionsPanel.Controls.Add(btnExportData);

            var lblInfo = new Label();
            lblInfo.Text = "Export includes:\n• All users\n• Attendance records\n• System statistics\n\n" +
                        "Files are saved to the application folder.";
            lblInfo.Location = new Point(10, 170);
            lblInfo.Size = new Size(260, 150);
            lblInfo.ForeColor = Color.Gray;
            actionsPanel.Controls.Add(lblInfo);

            page.Controls.Add(actionsPanel);

            return page;
        }

        private void RefreshStats()
        {
            var stats = db.GetStatistics();
            statsList.Items.Clear();

            statsList.Items.Add(new ListViewItem(new[] { "Total Active Users", stats["TotalUsers"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Total Students", stats["TotalStudents"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Total Teachers", stats["TotalTeachers"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Total Sessions", stats["TotalSessions"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Total Gaze Points", stats["TotalGazePoints"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Today's Sessions", stats["TodaySessions"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Active Users Today", stats["ActiveUsersToday"].ToString() }));
            statsList.Items.Add(new ListViewItem(new[] { "Database Last Modified", stats.ContainsKey("LastModified") ? stats["LastModified"].ToString() : "-" }));
        }

        private void BtnExportData_Click(object sender, EventArgs e)
        {
            try
            {
                string basePath = AppDomain.CurrentDomain.BaseDirectory;
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // Export Users
                var usersCsv = "ID,Name,Role,Bluetooth,CreatedAt,LastLogin\n";
                foreach (var user in db.GetAllUsers())
                {
                    usersCsv += $"{user.Id},{user.Name},{user.Role},{user.BluetoothAddress},{user.CreatedAt:yyyy-MM-dd HH:mm},{user.LastLogin:yyyy-MM-dd HH:mm}\n";
                }
                File.WriteAllText(Path.Combine(basePath, $"users_export_{timestamp}.csv"), usersCsv);

                // Export Attendance
                var attendanceCsv = "ID,UserID,UserName,LoginTime,LogoutTime,Duration,Scenes,Challenges\n";
                foreach (var record in db.GetAttendanceHistory())
                {
                    string duration = record.LogoutTime.HasValue ? (record.LogoutTime.Value - record.LoginTime).ToString() : "-";
                    attendanceCsv += $"{record.Id},{record.UserId},{record.UserName},{record.LoginTime:yyyy-MM-dd HH:mm},{record.LogoutTime:yyyy-MM-dd HH:mm},{duration},{record.ScenesCompleted},{record.ChallengesCompleted}\n";
                }
                File.WriteAllText(Path.Combine(basePath, $"attendance_export_{timestamp}.csv"), attendanceCsv);

                MessageBox.Show($"Data exported successfully!\nFiles saved to:\n{basePath}", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion
    }
}
