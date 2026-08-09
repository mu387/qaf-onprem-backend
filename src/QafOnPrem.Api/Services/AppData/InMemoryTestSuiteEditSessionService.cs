using System.Collections.Concurrent;
using System.Security.Claims;
using QafOnPrem.Api.Contracts;

namespace QafOnPrem.Api.Services.AppData;

public sealed class InMemoryTestSuiteEditSessionService : ITestSuiteEditSessionService
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(75);
    private readonly ConcurrentDictionary<long, SuiteSessionState> _suiteStates = new();

    public TestSuiteEditSessionStatusDto AcquireOrRefresh(ClaimsPrincipal principal, long testSuiteId, TestSuiteEditSessionRequest request)
    {
        var sessionId = NormalizeSessionId(request.SessionId);
        var requestEdit = request.RequestEdit != false;
        var currentUser = BuildUser(principal);
        var now = DateTimeOffset.UtcNow;
        var state = _suiteStates.GetOrAdd(testSuiteId, _ => new SuiteSessionState());

        lock (state.SyncRoot)
        {
            CleanupExpiredSessions(state, now);
            state.Sessions[sessionId] = new PresenceSession(sessionId, currentUser.Id, currentUser.Name, currentUser.Email, requestEdit, now);
            PromoteEditorIfNeeded(state);
            if (requestEdit && (!state.EditorUserId.HasValue || state.EditorUserId.Value == currentUser.Id))
            {
                state.EditorUserId = currentUser.Id;
            }

            return BuildStatus(testSuiteId, state, currentUser.Id, now);
        }
    }

    public void Release(ClaimsPrincipal principal, long testSuiteId, string sessionId)
    {
        if (!_suiteStates.TryGetValue(testSuiteId, out var state))
        {
            return;
        }

        var normalizedSessionId = NormalizeSessionId(sessionId);
        lock (state.SyncRoot)
        {
            state.Sessions.TryRemove(normalizedSessionId, out _);
            CleanupExpiredSessions(state, DateTimeOffset.UtcNow);
            PromoteEditorIfNeeded(state);
            if (state.Sessions.IsEmpty)
            {
                _suiteStates.TryRemove(testSuiteId, out _);
            }
        }
    }

    public void EnsureCanEdit(ClaimsPrincipal principal, long testSuiteId)
    {
        if (!_suiteStates.TryGetValue(testSuiteId, out var state))
        {
            return;
        }

        var currentUserId = GetRequiredUserId(principal);
        lock (state.SyncRoot)
        {
            CleanupExpiredSessions(state, DateTimeOffset.UtcNow);
            PromoteEditorIfNeeded(state);
            if (!state.EditorUserId.HasValue || state.EditorUserId.Value == currentUserId)
            {
                return;
            }

            var editor = BuildDistinctUsers(state).FirstOrDefault(user => user.Id == state.EditorUserId.Value);
            var editorName = editor?.Name ?? $"User {state.EditorUserId.Value}";
            throw new TestSuiteEditLockException($"This test is currently being edited by {editorName}. You are in read-only mode.");
        }
    }

    private static TestSuiteEditSessionStatusDto BuildStatus(long testSuiteId, SuiteSessionState state, long currentUserId, DateTimeOffset now)
    {
        CleanupExpiredSessions(state, now);
        PromoteEditorIfNeeded(state);

        var distinctUsers = BuildDistinctUsers(state);
        var editor = state.EditorUserId.HasValue
            ? distinctUsers.FirstOrDefault(user => user.Id == state.EditorUserId.Value)
            : null;
        var viewers = distinctUsers.Where(user => user.Id != editor?.Id).ToArray();
        var canEdit = !state.EditorUserId.HasValue || state.EditorUserId.Value == currentUserId;

        return new TestSuiteEditSessionStatusDto
        {
            TestSuiteId = testSuiteId,
            CanEdit = canEdit,
            IsEditor = state.EditorUserId.HasValue && state.EditorUserId.Value == currentUserId,
            Editor = editor,
            Viewers = viewers,
            ViewerCount = viewers.Length,
            ActiveUserCount = distinctUsers.Count,
        };
    }

    private static List<UserBasicDto> BuildDistinctUsers(SuiteSessionState state)
    {
        return state.Sessions.Values
            .GroupBy(session => session.UserId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.LastSeenUtc).First();
                return new UserBasicDto
                {
                    Id = latest.UserId,
                    Name = latest.UserName,
                    Email = latest.Email,
                };
            })
            .OrderBy(user => user.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void PromoteEditorIfNeeded(SuiteSessionState state)
    {
        if (state.EditorUserId.HasValue)
        {
            var editorStillPresent = state.Sessions.Values.Any(session => session.UserId == state.EditorUserId.Value && session.RequestEdit);
            if (editorStillPresent)
            {
                return;
            }

            state.EditorUserId = null;
        }

        var nextEditor = state.Sessions.Values
            .Where(session => session.RequestEdit)
            .OrderBy(session => session.LastSeenUtc)
            .FirstOrDefault();
        state.EditorUserId = nextEditor?.UserId;
    }

    private static void CleanupExpiredSessions(SuiteSessionState state, DateTimeOffset now)
    {
        var expiredKeys = state.Sessions.Values
            .Where(session => now - session.LastSeenUtc > SessionTimeout)
            .Select(session => session.SessionId)
            .ToArray();

        foreach (var expiredKey in expiredKeys)
        {
            state.Sessions.TryRemove(expiredKey, out _);
        }
    }

    private static string NormalizeSessionId(string? sessionId)
    {
        var normalized = sessionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("A session_id is required.");
        }

        return normalized;
    }

    private static SessionUser BuildUser(ClaimsPrincipal principal)
    {
        var userId = GetRequiredUserId(principal);
        var name = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("unique_name")
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.Identity?.Name
            ?? $"User {userId}";
        var email = principal.FindFirstValue(ClaimTypes.Email);
        return new SessionUser(userId, name, email);
    }

    private static long GetRequiredUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        if (!long.TryParse(raw, out var userId))
        {
            throw new InvalidOperationException("Authenticated user id is missing.");
        }

        return userId;
    }

    private sealed class SuiteSessionState
    {
        public object SyncRoot { get; } = new();

        public ConcurrentDictionary<string, PresenceSession> Sessions { get; } = new(StringComparer.Ordinal);

        public long? EditorUserId { get; set; }
    }

    private sealed record PresenceSession(string SessionId, long UserId, string UserName, string? Email, bool RequestEdit, DateTimeOffset LastSeenUtc);

    private sealed record SessionUser(long Id, string Name, string? Email);
}