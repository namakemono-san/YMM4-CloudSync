# Code Review Recommendations

## Quick Reference

This document provides actionable recommendations from the comprehensive code review. See [CODE_REVIEW_FINDINGS.md](./CODE_REVIEW_FINDINGS.md) for detailed analysis.

> **Status Update (2026-01-12):** Priority 1-2 recommendations have been implemented in commit `455abb8`.

## ✅ Priority 1 - COMPLETED

### 1. Improve Exception Logging ✅ IMPLEMENTED

**Files affected:** GoogleDriveService.cs, YmmxPacker.cs, DropboxService.cs, YmmxExtractor.cs

**Implemented:**
```csharp
catch (Exception ex)
{
    System.Diagnostics.Debug.WriteLine($"[Context] Error: {ex.Message}");
}
```

**Status:** All empty catch blocks now include Debug.WriteLine logging for easier debugging.

---

## ✅ Priority 2 - COMPLETED

### 2. Extract Magic Numbers to Constants ✅ IMPLEMENTED

**Files affected:** OneDriveService.cs, DropboxService.cs, YmmxPacker.cs, YmmxExtractor.cs

**Implemented:**
```csharp
// At class level with documentation
/// <summary>
/// OneDrive recommends chunk sizes above this threshold for optimal upload.
/// Files smaller than 4MB can be uploaded in a single request.
/// See: https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession
/// </summary>
private const long ChunkThresholdBytes = 4 * 1024 * 1024; // 4MB
```

**Status:** All magic numbers extracted to well-documented constants:
- `OneDriveService`: `ChunkThresholdBytes` (4MB)
- `DropboxService`: `UploadLimitBytes` (150MB), `ChunkSizeBytes` (8MB)
- `YmmxPacker`: `ExtraSpaceReserveBytes` (20MB)
- `YmmxExtractor`: `ExtraSpaceReserveBytes` (20MB)

---

### 3. Document Sentry DSN in Configuration ✅ IMPLEMENTED

**File:** README.md

**Implemented:** Added configuration section explaining:
- Sentry DSN is intentionally public and safe
- No PII is collected (SendDefaultPii: false)
- Instructions to disable error reporting if desired

**Status:** Complete

---

### 4. Remove Dead Code ✅ IMPLEMENTED

**File:** YmmxPacker.cs, line 184

**Fixed:** Removed unused `Path.Combine(assetsDir, subFolder);` statement.

**Status:** Complete

---

## Priority 3 - Enhancement (Nice to have)

### 5. Add Unit Tests

**Recommendation:** Start with utility classes that have no dependencies:

```csharp
// Test structure example
[TestClass]
public class HashHelperTests
{
    [TestMethod]
    public void ComputeDirectoryHash_SameContent_ReturnsSameHash()
    {
        // Arrange
        var dir1 = CreateTestDirectory(/* ... */);
        var dir2 = CreateTestDirectory(/* ... */);
        
        // Act
        var hash1 = HashHelper.ComputeDirectoryHash(dir1, false, null, 0, ref bytes);
        var hash2 = HashHelper.ComputeDirectoryHash(dir2, false, null, 0, ref bytes);
        
        // Assert
        Assert.AreEqual(hash1, hash2);
    }
}
```

**Suggested test coverage:**
- HashHelper - directory hashing determinism
- PathHelper - path validation and normalization  
- RetryHelper - retry logic and exponential backoff
- DiskSpaceHelper - size formatting and space checks

---

### 6. Consolidate Temporary File Handling

**Pattern found in:** GoogleDriveService.cs, OneDriveService.cs, DropboxService.cs

**Current pattern:**
```csharp
var tempPath = localPath + ".tmp";
try
{
    // download to tempPath
    File.Move(tempPath, localPath);
}
catch
{
    if (File.Exists(tempPath))
        File.Delete(tempPath);
    throw;
}
```

**Recommended:** Extract to shared helper:
```csharp
public static class SafeFileDownloader
{
    public static async Task DownloadWithTempFileAsync(
        string destinationPath,
        Func<string, Task> downloadAction)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            await downloadAction(tempPath);
            
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
                
            File.Move(tempPath, destinationPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* Cleanup failed, but original error is more important */ }
            }
            throw;
        }
    }
}
```

---

### 7. Add Architecture Documentation

**Create:** `ARCHITECTURE.md`

**Suggested sections:**
- Component overview diagram
- Data flow for upload/download operations
- Authentication flow for each cloud service
- YMMX file format specification
- Plugin integration with YMM4

---

### 8. Improve Temp Directory Cleanup

**File:** Plugin.cs, lines 87-91

**Current:**
```csharp
var directories = Directory.GetDirectories(tempPath, "ymmx_*");
foreach (var dir in directories)
{
    Directory.Delete(dir, true);
}
```

**Recommended:**
```csharp
var directories = Directory.GetDirectories(tempPath, "ymmx_*");
var cutoffTime = DateTime.Now.AddDays(-7);

foreach (var dir in directories)
{
    try
    {
        var dirInfo = new DirectoryInfo(dir);
        if (dirInfo.CreationTime < cutoffTime)
        {
            Directory.Delete(dir, true);
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[Cleanup] Failed to delete {dir}: {ex.Message}");
    }
}
```

**Rationale:** Avoids deleting directories from currently running operations and provides better error handling.

---

## Non-Issues (Clarifications)

### Static HttpClient Usage ✅
The code correctly uses static `HttpClient` instances to prevent socket exhaustion. This is best practice.

### Lock Usage in OneDriveService ✅
Uses .NET 9+ `Lock` class correctly for thread-safe token cache access. This is modern and correct.

### Japanese Error Messages ✅
Appropriate for target audience (Japanese YMM4 users). No change needed.

### Credential Files in .gitignore ✅
Properly excluded from version control with example files provided. This is correct.

### DPAPI Usage for Encryption ✅
Using Windows `ProtectedData` with `CurrentUser` scope is appropriate for this Windows-only application.

---

## Implementation Status

### ✅ Completed (Commit 455abb8)

**Priority 1-2 items:**
- ✅ Remove dead code (Priority 2, Item 4)
- ✅ Add exception logging to critical paths (Priority 1, Item 1)
- ✅ Extract magic numbers (Priority 2, Item 2)
- ✅ Document Sentry DSN (Priority 2, Item 3)

### 🔄 Remaining Recommendations

**Priority 3 (Optional enhancements):**
- Add unit tests for utilities (Priority 3, Item 5)
- Consolidate temp file handling (Priority 3, Item 6)
- Improve documentation (Priority 3, Items 7)
- Enhance cleanup logic (Priority 3, Item 8)

---

## Questions?

For questions about these recommendations, please:
1. Check [CODE_REVIEW_FINDINGS.md](./CODE_REVIEW_FINDINGS.md) for detailed context
2. Open a GitHub issue for discussion
3. Reference the specific recommendation number in your question

---

*Last updated: January 12, 2026*
*Priority 1-2 recommendations completed in commit 455abb8*
