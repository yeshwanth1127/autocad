using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace autocad_final.UI
{
    /// <summary>
    /// Persists AI chat sessions and messages to a local JSON file (no native SQLite DLL).
    /// Data lives under %APPDATA%\autocad-final\chat_store.json.
    /// Thread-safety: call only from the UI thread (same as before).
    /// </summary>
    public sealed class ChatStore : IDisposable
    {
        public static readonly string StoreDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autocad-final");

        /// <summary>Current persistence file.</summary>
        public static readonly string JsonPath = Path.Combine(StoreDirectory, "chat_store.json");

        /// <summary>Legacy SQLite path (older builds); kept so help text stays accurate.</summary>
        public static readonly string DbPath = Path.Combine(StoreDirectory, "chat_history.db");

        private readonly object _sync = new object();
        private readonly ChatStoreRoot _root;
        private bool _disposed;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(
                typeof(ChatStoreRoot),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public ChatStore()
        {
            Directory.CreateDirectory(StoreDirectory);
            _root = LoadFromDisk() ?? new ChatStoreRoot { Version = 1, Sessions = new List<SessionDto>() };
        }

        public long CreateSession(string title = "")
        {
            lock (_sync)
            {
                long nextId = _root.Sessions == null || _root.Sessions.Count == 0
                    ? 1
                    : _root.Sessions.Max(x => x.Id) + 1;
                var s = new SessionDto
                {
                    Id        = nextId,
                    CreatedAt = Now(),
                    Title     = title ?? string.Empty,
                    Messages  = new List<MessageDto>()
                };
                if (_root.Sessions == null)
                    _root.Sessions = new List<SessionDto>();
                _root.Sessions.Add(s);
                SaveLocked();
                return nextId;
            }
        }

        public void UpdateTitle(long sessionId, string title)
        {
            lock (_sync)
            {
                var s = FindSession(sessionId);
                if (s == null) return;
                s.Title = title ?? string.Empty;
                SaveLocked();
            }
        }

        public List<SessionRow> GetSessions()
        {
            lock (_sync)
            {
                var list = new List<SessionRow>();
                if (_root.Sessions == null)
                    return list;
                foreach (var s in _root.Sessions.OrderByDescending(x => x.Id))
                {
                    list.Add(new SessionRow
                    {
                        Id        = s.Id,
                        CreatedAt = ParseUtc(s.CreatedAt),
                        Title     = s.Title ?? string.Empty
                    });
                }

                return list;
            }
        }

        public void DeleteSession(long sessionId)
        {
            lock (_sync)
            {
                if (_root.Sessions == null) return;
                _root.Sessions.RemoveAll(s => s.Id == sessionId);
                SaveLocked();
            }
        }

        public void AddMessage(long sessionId, string role, string content)
        {
            lock (_sync)
            {
                var s = FindSession(sessionId);
                if (s == null) return;
                if (s.Messages == null)
                    s.Messages = new List<MessageDto>();
                s.Messages.Add(new MessageDto
                {
                    Role      = role ?? string.Empty,
                    Content   = content ?? string.Empty,
                    CreatedAt = Now()
                });
                SaveLocked();
            }
        }

        public List<MessageRow> GetMessages(long sessionId)
        {
            lock (_sync)
            {
                var list = new List<MessageRow>();
                var s = FindSession(sessionId);
                if (s?.Messages == null)
                    return list;
                long mid = 0;
                foreach (var m in s.Messages)
                {
                    mid++;
                    list.Add(new MessageRow
                    {
                        Id        = mid,
                        Role      = m.Role ?? string.Empty,
                        Content   = m.Content ?? string.Empty,
                        CreatedAt = ParseUtc(m.CreatedAt)
                    });
                }

                return list;
            }
        }

        private SessionDto FindSession(long sessionId)
        {
            return _root.Sessions?.FirstOrDefault(s => s.Id == sessionId);
        }

        private void SaveLocked()
        {
            string tmp = JsonPath + ".tmp";
            using (var fs = File.Create(tmp))
            {
                Serializer.WriteObject(fs, _root);
            }

            if (File.Exists(JsonPath))
                File.Replace(tmp, JsonPath, null);
            else
                File.Move(tmp, JsonPath);
        }

        private static ChatStoreRoot LoadFromDisk()
        {
            if (!File.Exists(JsonPath))
                return null;
            try
            {
                using (var fs = File.OpenRead(JsonPath))
                {
                    return Serializer.ReadObject(fs) as ChatStoreRoot;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        private static DateTime ParseUtc(string s)
        {
            if (string.IsNullOrEmpty(s))
                return DateTime.MinValue;
            return DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime()
                : DateTime.MinValue;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    [DataContract]
    internal sealed class ChatStoreRoot
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public List<SessionDto> Sessions { get; set; }
    }

    [DataContract]
    internal sealed class SessionDto
    {
        [DataMember] public long Id { get; set; }
        [DataMember] public string CreatedAt { get; set; }
        [DataMember] public string Title { get; set; }
        [DataMember] public List<MessageDto> Messages { get; set; }
    }

    [DataContract]
    internal sealed class MessageDto
    {
        [DataMember] public string Role { get; set; }
        [DataMember] public string Content { get; set; }
        [DataMember] public string CreatedAt { get; set; }
    }

    public sealed class SessionRow
    {
        public long     Id        { get; set; }
        public DateTime CreatedAt { get; set; }
        public string   Title     { get; set; }

        public override string ToString()
        {
            string label = string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title;
            if (label.Length > 42) label = label.Substring(0, 42) + "…";
            return CreatedAt.ToString("MMM d, HH:mm") + "  —  " + label;
        }
    }

    public sealed class MessageRow
    {
        public long     Id        { get; set; }
        public string   Role      { get; set; }
        public string   Content   { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
