# iCloudSync

iCloudSync is a lightweight Windows tool that automatically syncs your iCloud to a specified local folder. 
It is designed to give you a **local copy of your iCloud storage**.

---

## Features

- Sync files from iCloud to a local folder
- Auto-sync with customizable schedule
- Can start with windows and run in the background

---

## Requirements

- Windows 10 or later
- iCloud installed and running on your PC
- [.NET 9 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) (only if using framework-dependent build)

---

## Installation & Usage

### Option A — Download Pre-Built Release

1. Go to the [Releases](https://github.com/NotNecrotic/iCloudSync/releases) page  
2. Download the latest `iCloudSync.exe`  
3. Run the EXE (no .NET installation needed if using self-contained build)  
4. Make sure iCloud is running on your PC 
5. Select both iCloud folders and their destinations
6. Enjoy! 

---

### Option B — Build from Source

1. Clone the repository:
   ```bash
   git clone https://github.com/NotNecrotic/iCloudSync.git
2. Navigate to the project folder:
   ```bash
   cd iCloudSync
4. Build the project (Release):
   ```bash
   dotnet build -c Release
6. Run the app:
   ```bash
   dotnet run --project iCloudSync
