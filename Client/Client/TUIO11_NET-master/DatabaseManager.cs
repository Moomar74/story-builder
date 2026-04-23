using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace TUIO
{
    /// <summary>
    /// Simple JSON-based database manager for the Story Builder application.
    /// Replaces the flat users.txt file with a structured database supporting
    /// users, attendance, and gaze tracking data.
    /// </summary>
    public class DatabaseManager
    {
        private readonly string dbFilePath;
        private DatabaseData data;

        public DatabaseManager(string baseDirectory)
        {
            dbFilePath = Path.Combine(baseDirectory, "storybuilder.db.json");
            Load();
        }

        #region Data Structures

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Role { get; set; }  // "Student", "Teacher", "Guest"
            public string BluetoothAddress { get; set; }
            public string MarkerId { get; set; }  // Optional marker assignment
            public DateTime CreatedAt { get; set; }
            public DateTime LastLogin { get; set; }
            public bool IsActive { get; set; } = true;
            public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        }

        public class AttendanceRecord
        {
            public int Id { get; set; }
            public int UserId { get; set; }
            public string UserName { get; set; }
            public DateTime LoginTime { get; set; }
            public DateTime? LogoutTime { get; set; }
            public string SessionType { get; set; }  // "Auto", "Manual", "Bluetooth"
            public int ScenesCompleted { get; set; }
            public int ChallengesCompleted { get; set; }
        }

        public class GazeDataRecord
        {
            public int Id { get; set; }
            public int UserId { get; set; }
            public string Page { get; set; }
            public float GazeX { get; set; }
            public float GazeY { get; set; }
            public DateTime Timestamp { get; set; }
            public bool IsBlink { get; set; }
        }

        private class DatabaseData
        {
            public int LastUserId { get; set; } = 0;
            public int LastAttendanceId { get; set; } = 0;
            public int LastGazeId { get; set; } = 0;
            public List<User> Users { get; set; } = new List<User>();
            public List<AttendanceRecord> Attendance { get; set; } = new List<AttendanceRecord>();
            public List<GazeDataRecord> GazeData { get; set; } = new List<GazeDataRecord>();
            public DateTime LastModified { get; set; } = DateTime.Now;
        }

        #endregion

        #region Core Operations

        private void Load()
        {
            if (File.Exists(dbFilePath))
            {
                try
                {
                    string json = File.ReadAllText(dbFilePath);
                    data = JsonConvert.DeserializeObject<DatabaseData>(json);
                    if (data == null) data = new DatabaseData();
                }
                catch
                {
                    data = new DatabaseData();
                }
            }
            else
            {
                data = new DatabaseData();
                Save();
            }
        }

        public void Save()
        {
            try
            {
                data.LastModified = DateTime.Now;
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(dbFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Database save error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region User CRUD Operations

        public List<User> GetAllUsers()
        {
            return data.Users.Where(u => u.IsActive).ToList();
        }

        public List<User> GetStudents()
        {
            return data.Users.Where(u => u.IsActive && u.Role == "Student").ToList();
        }

        public List<User> GetTeachers()
        {
            return data.Users.Where(u => u.IsActive && u.Role == "Teacher").ToList();
        }

        public User GetUserById(int id)
        {
            return data.Users.FirstOrDefault(u => u.Id == id && u.IsActive);
        }

        public User GetUserByName(string name)
        {
            return data.Users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && u.IsActive);
        }

        public User GetUserByBluetooth(string btAddress)
        {
            if (string.IsNullOrEmpty(btAddress)) return null;
            return data.Users.FirstOrDefault(u => 
                u.BluetoothAddress != null && 
                u.BluetoothAddress.Equals(btAddress, StringComparison.OrdinalIgnoreCase) && 
                u.IsActive);
        }

        public int GetNextUserId()
        {
            data.LastUserId++;
            return data.LastUserId;
        }

        public User CreateUser(string name, string role = "Student", string btAddress = null)
        {
            // Check for duplicate name
            if (GetUserByName(name) != null)
                throw new Exception("A user with this name already exists.");

            var user = new User
            {
                Id = GetNextUserId(),
                Name = name,
                Role = role,
                BluetoothAddress = btAddress,
                CreatedAt = DateTime.Now,
                LastLogin = DateTime.MinValue,
                IsActive = true
            };

            data.Users.Add(user);
            Save();
            return user;
        }

        public void UpdateUser(User user)
        {
            var existing = data.Users.FirstOrDefault(u => u.Id == user.Id);
            if (existing != null)
            {
                existing.Name = user.Name;
                existing.Role = user.Role;
                existing.BluetoothAddress = user.BluetoothAddress;
                existing.MarkerId = user.MarkerId;
                existing.IsActive = user.IsActive;
                existing.Metadata = user.Metadata;
                Save();
            }
        }

        public void DeleteUser(int id)
        {
            var user = data.Users.FirstOrDefault(u => u.Id == id);
            if (user != null)
            {
                user.IsActive = false;  // Soft delete
                Save();
            }
        }

        public void RecordLogin(int userId)
        {
            var user = GetUserById(userId);
            if (user != null)
            {
                user.LastLogin = DateTime.Now;
                Save();
            }
        }

        public bool IsTeacher(int userId)
        {
            var user = GetUserById(userId);
            return user != null && user.Role == "Teacher";
        }

        #endregion

        #region Attendance Operations

        public int StartAttendanceSession(int userId, string sessionType = "Manual")
        {
            var user = GetUserById(userId);
            if (user == null) return -1;

            data.LastAttendanceId++;
            var record = new AttendanceRecord
            {
                Id = data.LastAttendanceId,
                UserId = userId,
                UserName = user.Name,
                LoginTime = DateTime.Now,
                SessionType = sessionType,
                ScenesCompleted = 0,
                ChallengesCompleted = 0
            };

            data.Attendance.Add(record);
            Save();
            return record.Id;
        }

        public void EndAttendanceSession(int attendanceId, int scenesCompleted = 0, int challengesCompleted = 0)
        {
            var record = data.Attendance.FirstOrDefault(a => a.Id == attendanceId);
            if (record != null)
            {
                record.LogoutTime = DateTime.Now;
                record.ScenesCompleted = scenesCompleted;
                record.ChallengesCompleted = challengesCompleted;
                Save();
            }
        }

        public List<AttendanceRecord> GetAttendanceHistory(int? userId = null, DateTime? fromDate = null, DateTime? toDate = null)
        {
            var query = data.Attendance.AsEnumerable();

            if (userId.HasValue)
                query = query.Where(a => a.UserId == userId.Value);

            if (fromDate.HasValue)
                query = query.Where(a => a.LoginTime >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(a => a.LoginTime <= toDate.Value);

            return query.OrderByDescending(a => a.LoginTime).ToList();
        }

        #endregion

        #region Gaze Data Operations

        public void RecordGazeData(int userId, string page, float gazeX, float gazeY, bool isBlink = false)
        {
            data.LastGazeId++;
            var record = new GazeDataRecord
            {
                Id = data.LastGazeId,
                UserId = userId,
                Page = page,
                GazeX = gazeX,
                GazeY = gazeY,
                Timestamp = DateTime.Now,
                IsBlink = isBlink
            };

            data.GazeData.Add(record);
            // Don't save on every gaze point - batch save would be better
            // Save every 100 records
            if (data.LastGazeId % 100 == 0)
                Save();
        }

        public List<GazeDataRecord> GetGazeData(int? userId = null, string page = null, DateTime? fromDate = null)
        {
            var query = data.GazeData.AsEnumerable();

            if (userId.HasValue)
                query = query.Where(g => g.UserId == userId.Value);

            if (!string.IsNullOrEmpty(page))
                query = query.Where(g => g.Page == page);

            if (fromDate.HasValue)
                query = query.Where(g => g.Timestamp >= fromDate.Value);

            return query.OrderByDescending(g => g.Timestamp).ToList();
        }

        public void ClearGazeData(int? userId = null, string page = null)
        {
            var query = data.GazeData.AsEnumerable();

            if (userId.HasValue)
                query = query.Where(g => g.UserId == userId.Value);

            if (!string.IsNullOrEmpty(page))
                query = query.Where(g => g.Page == page);

            var toRemove = query.ToList();
            foreach (var item in toRemove)
                data.GazeData.Remove(item);

            Save();
        }

        #endregion

        #region Migration from old format

        public void MigrateFromOldFormat(string usersFilePath)
        {
            if (!File.Exists(usersFilePath)) return;

            try
            {
                foreach (string line in File.ReadAllLines(usersFilePath))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 2)
                    {
                        int id;
                        if (int.TryParse(parts[0], out id))
                        {
                            string name = parts[1];
                            string btAddr = parts.Length >= 3 ? parts[2] : null;
                            if (btAddr == "NONE") btAddr = null;

                            // Check if user already exists
                            if (GetUserByName(name) == null)
                            {
                                var user = new User
                                {
                                    Id = id,
                                    Name = name,
                                    Role = id == 36 ? "Teacher" : "Student",
                                    BluetoothAddress = btAddr,
                                    CreatedAt = DateTime.Now,
                                    LastLogin = DateTime.MinValue,
                                    IsActive = true
                                };
                                data.Users.Add(user);
                                if (id > data.LastUserId) data.LastUserId = id;
                            }
                        }
                    }
                }
                Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Migration error: " + ex.Message, "Migration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #endregion

        #region Statistics

        public Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>();

            stats["TotalUsers"] = data.Users.Count(u => u.IsActive);
            stats["TotalStudents"] = data.Users.Count(u => u.IsActive && u.Role == "Student");
            stats["TotalTeachers"] = data.Users.Count(u => u.IsActive && u.Role == "Teacher");
            stats["TotalSessions"] = data.Attendance.Count;
            stats["TotalGazePoints"] = data.GazeData.Count;

            // Today's activity
            var today = DateTime.Today;
            stats["TodaySessions"] = data.Attendance.Count(a => a.LoginTime.Date == today);
            stats["ActiveUsersToday"] = data.Attendance.Where(a => a.LoginTime.Date == today).Select(a => a.UserId).Distinct().Count();

            return stats;
        }

        #endregion
    }
}
