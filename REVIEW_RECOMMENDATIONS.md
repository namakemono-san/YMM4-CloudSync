# Code Review Recommendations

## Quick Reference

This document provides actionable recommendations from the comprehensive code review. See [CODE_REVIEW_FINDINGS.md](./CODE_REVIEW_FINDINGS.md) for detailed analysis.

## Priority 1 - Critical (Recommended for immediate action)

### 1. Improve Exception Logging

**Files affected:** GoogleDriveService.cs, YmmxPacker.cs, DropboxService.cs, and others

**Current:**
```csharp
catch
{
    // ignored
}
```

**Recommended:**
```csharp
catch (Exception ex)
{
    Debug.WriteLine($"[Context] Error: {ex.Message}");
    // or for non-critical paths:
    SentrySdk.CaptureException(ex);
}
```

**Rationale:** Silent exception swallowing makes debugging extremely difficult.

---

## Priority 2 - Important (Should address soon)

### 2. Extract Magic Numbers to Constants

**Files affected:** OneDriveService.cs, DropboxService.cs, YmmxPacker.cs

**Current:**
```csharp
const long chunkThreshold = 4 * 1024 * 1024;
```

**Recommended:**
```csharp
// At class level with documentation
/// <summary>
/// OneDrive recommends chunk sizes above this threshold for optimal upload.
/// Files smaller than 4MB can be uploaded in a single request.
/// </summary>
private const long ChunkThresholdBytes = 4 * 1024 * 1024; // 4MB
```

**Rationale:** Improves code maintainability and makes intent clear.

---

### 3. Document Sentry DSN in Configuration

**File:** appsettings.json

**Add a README section:**
```markdown
## Configuration

### Sentry DSN
The Sentry DSN in `appsettings.json` is intentionally public. Sentry DSNs are
safe to expose in client applications and are required for error reporting.
```

**Rationale:** Clarifies intentional design decision.

---

### 4. Remove Dead Code

**File:** YmmxPacker.cs, line 184

**Current:**
```csharp
var subFolder = folder ?? "other";
Path.Combine(assetsDir, subFolder);  // ← This does nothing
```

**Fix:** Remove the unused line or assign to a variable if needed.

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

## Implementation Order

For teams looking to implement these recommendations:

**Week 1:**
- Remove dead code (Priority 2, Item 4)
- Add exception logging to critical paths (Priority 1, Item 1)

**Week 2:**  
- Extract magic numbers (Priority 2, Item 2)
- Document Sentry DSN (Priority 2, Item 3)

**Week 3-4:**
- Add unit tests for utilities (Priority 3, Item 5)
- Consolidate temp file handling (Priority 3, Item 6)

**Ongoing:**
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
