# Database Integration for Story Builder

This document describes the new database functionality that replaces the flat `users.txt` file with a structured JSON database.

## Overview

The application now uses a JSON-based database (`storybuilder.db.json`) to store:
- **Users** (Students and Teachers) with roles
- **Attendance Records** (login/logout times, session data)
- **Gaze Tracking Data** (optional, stores where users look on screen)

## Features

### 1. User Roles
- **Student**: Regular users who can play stories
- **Teacher**: Has access to the Teacher Management Panel for CRUD operations
- **Guest**: Marker 10 for quick access without registration

### 2. Teacher Management Panel (Press F4)
Teachers can:
- **Create** new students with name, role, and optional Bluetooth address
- **Read** all user records with filtering and sorting
- **Update** existing user information
- **Delete** users (soft delete - marks as inactive)
- **View Attendance Reports** with date range filtering
- **Export Data** to CSV format

### 3. Attendance Tracking
- Automatically logs when users sign in
- Records login time, logout time, scenes completed, challenges completed
- Tracks session type (Manual, Bluetooth Auto-Login, etc.)
- Viewable in Teacher Panel with statistics

### 4. Migration from Old Format
The system automatically migrates existing `users.txt` data:
- Format: `ID|Name|BluetoothAddress`
- Marker 36 is automatically assigned the "Teacher" role
- All other users become "Students"

## File Structure

```
storybuilder.db.json
├── LastUserId: number
├── LastAttendanceId: number
├── LastGazeId: number
├── Users: [
│   {
│     Id: number,
│     Name: string,
│     Role: "Student" | "Teacher" | "Guest",
│     BluetoothAddress: string,
│     CreatedAt: datetime,
│     LastLogin: datetime,
│     IsActive: boolean
│   }
]
├── Attendance: [
│   {
│     Id: number,
│     UserId: number,
│     UserName: string,
│     LoginTime: datetime,
│     LogoutTime: datetime,
│     SessionType: string,
│     ScenesCompleted: number,
│     ChallengesCompleted: number
│   }
]
└── GazeData: [...]  // Optional gaze tracking points
```

## TUIO Marker Controls (No Keyboard)

All interactions are now marker-based:

| Marker | Function | Who Can Use |
|--------|----------|-------------|
| 10 | Guest Login | Anyone |
| 34 | Back / Escape | Anyone |
| 35 | Sign Up (on Sign In screen) | Anyone |
| 36 | Teacher Login | Teachers |
| 37 | Toggle Fullscreen | Anyone |
| 38 | Toggle Gaze Heatmap | Anyone |
| 39 | Clear Heatmap Data | Anyone |
| 40 | Open Teacher Panel | Teachers only |

## Setting Up a Teacher

1. Place marker 36 on the table
2. Rotate 45° to sign in
3. Place marker 40 to open Teacher Panel
4. Use the "Students Management" tab to add/edit students

## Adding a New Student

### Method 1: Teacher Panel (Recommended)
1. Teacher places Marker 40 to open panel
2. Click "Students Management" tab
3. Enter student name, select role "Student"
4. (Optional) Add Bluetooth address for auto-login
5. Click "Add User"
6. Note the assigned ID - this is their marker ID

### Method 2: Self-Registration (Sign Up)
1. Place marker 35 on Sign In screen
2. Use markers 0-24 to spell name (A-Y)
3. Hold marker 25 for 2 seconds to confirm
4. System assigns next available ID

## Database Backup

The database file `storybuilder.db.json` is stored in the application folder. To backup:
1. Close the application
2. Copy `storybuilder.db.json` to a safe location
3. Also backup `attendance_log.txt` for additional redundancy

## Exporting Data

Teachers can export data to CSV:
1. Press F4 to open Teacher Panel
2. Go to "Statistics & Export" tab
3. Click "Export to CSV"
4. Files are saved in the application folder:
   - `users_export_YYYYMMDD_HHMMSS.csv`
   - `attendance_export_YYYYMMDD_HHMMSS.csv`

## Troubleshooting

### Missing Newtonsoft.Json
If you get a compile error about missing Newtonsoft.Json:
1. In Visual Studio: Tools → NuGet Package Manager → Package Manager Console
2. Run: `Install-Package Newtonsoft.Json -Version 13.0.3`

### Database Corruption
If the database becomes corrupted:
1. Close the application
2. Delete or rename `storybuilder.db.json`
3. Restart - the system will create a new database
4. If you have a `users.txt` file, it will be auto-migrated

### Teacher Panel Won't Open
- Ensure you're logged in as Teacher (Marker 36)
- Check that `Newtonsoft.Json.dll` is in the bin folder
- Look for any error messages in the application logs
