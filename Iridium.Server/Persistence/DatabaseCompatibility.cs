using System.Data;
using Iridium.Protocol;
using Iridium.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Persistence;

public static class DatabaseCompatibility
{
    public static async Task EnsureAvatarPresetSchemaAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "Accounts", "ActiveAvatarPresetId", "TEXT NULL");
        await EnsureColumnAsync(db, "Accounts", "AvatarRevision", "INTEGER NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AccountAvatarPresets (
                Id TEXT NOT NULL CONSTRAINT PK_AccountAvatarPresets PRIMARY KEY,
                AccountId TEXT NOT NULL,
                SlotIndex INTEGER NOT NULL,
                OriginalObjectKey TEXT NOT NULL,
                ProcessedObjectKey TEXT NULL,
                ContentType TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                CropX REAL NOT NULL DEFAULT 0,
                CropY REAL NOT NULL DEFAULT 0,
                Zoom REAL NOT NULL DEFAULT 1,
                Revision INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                CONSTRAINT FK_AccountAvatarPresets_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AccountAvatarPresets_AccountId_SlotIndex
                ON AccountAvatarPresets (AccountId, SlotIndex);
            """);
    }

    public static async Task EnsureBannerPresetSchemaAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "Accounts", "ActiveBannerPresetId", "TEXT NULL");
        await EnsureColumnAsync(db, "Accounts", "BannerRevision", "INTEGER NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AccountBannerPresets (
                Id TEXT NOT NULL CONSTRAINT PK_AccountBannerPresets PRIMARY KEY,
                AccountId TEXT NOT NULL,
                SlotIndex INTEGER NOT NULL,
                OriginalObjectKey TEXT NOT NULL,
                ProcessedObjectKey TEXT NULL,
                ContentType TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                CropX REAL NOT NULL DEFAULT 0,
                CropY REAL NOT NULL DEFAULT 0,
                Zoom REAL NOT NULL DEFAULT 1,
                Revision INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                CONSTRAINT FK_AccountBannerPresets_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AccountBannerPresets_AccountId_SlotIndex
                ON AccountBannerPresets (AccountId, SlotIndex);
            """);
    }

    public static async Task EnsureCommunityMediaSchemaAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "Communities", "ActiveAvatarPresetId", "TEXT NULL");
        await EnsureColumnAsync(db, "Communities", "AvatarRevision", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "Communities", "ActiveBannerPresetId", "TEXT NULL");
        await EnsureColumnAsync(db, "Communities", "BannerRevision", "INTEGER NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CommunityMediaPresets (
                Id TEXT NOT NULL CONSTRAINT PK_CommunityMediaPresets PRIMARY KEY,
                CommunityId TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                SlotIndex INTEGER NOT NULL,
                OriginalObjectKey TEXT NOT NULL,
                ProcessedObjectKey TEXT NULL,
                ContentType TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                CropX REAL NOT NULL DEFAULT 0,
                CropY REAL NOT NULL DEFAULT 0,
                Zoom REAL NOT NULL DEFAULT 1,
                Revision INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                CONSTRAINT FK_CommunityMediaPresets_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunityMediaPresets_CommunityId_Kind_SlotIndex
                ON CommunityMediaPresets (CommunityId, Kind, SlotIndex);
            """);
    }

    public static async Task EnsureCommunityEmojiSchemaAsync(IridiumDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CommunityEmojis (
                Id TEXT NOT NULL CONSTRAINT PK_CommunityEmojis PRIMARY KEY,
                CommunityId TEXT NOT NULL,
                Name TEXT NOT NULL,
                ObjectKey TEXT NOT NULL,
                ContentType TEXT NOT NULL,
                IsAnimated INTEGER NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                SizeBytes INTEGER NOT NULL,
                Revision INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CreatedByAccountId TEXT NOT NULL,
                CONSTRAINT FK_CommunityEmojis_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunityEmojis_CommunityId_Name
                ON CommunityEmojis (CommunityId, Name);
            """);
    }

    public static async Task EnsureCommunityManagementSchemaAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "Accounts", "Description", "TEXT NULL");
        await EnsureColumnAsync(db, "CommunityRoles", "Position", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "CommunityRoles", "IsDefault", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "CommunityRoles", "Color", "TEXT NULL");
        await EnsureColumnAsync(db, "CommunityRoles", "DisplaySeparately", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "CommunityRoles", "IsMentionable", "INTEGER NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_CommunityRoles_CommunityId_Position
                ON CommunityRoles (CommunityId, Position);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunityRoles_CommunityId_IsDefault
                ON CommunityRoles (CommunityId) WHERE IsDefault = 1;
            CREATE TABLE IF NOT EXISTS CommunityInvites (
                Id TEXT NOT NULL CONSTRAINT PK_CommunityInvites PRIMARY KEY,
                CommunityId TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                CodePrefix TEXT NOT NULL,
                CreatedByAccountId TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NULL,
                MaxUses INTEGER NULL,
                Uses INTEGER NOT NULL DEFAULT 0,
                Revoked INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT FK_CommunityInvites_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE,
                CONSTRAINT FK_CommunityInvites_Accounts_CreatedByAccountId FOREIGN KEY (CreatedByAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunityInvites_TokenHash ON CommunityInvites (TokenHash);
            CREATE INDEX IF NOT EXISTS IX_CommunityInvites_CommunityId_Revoked ON CommunityInvites (CommunityId, Revoked);
            CREATE TABLE IF NOT EXISTS CommunityBans (
                CommunityId TEXT NOT NULL,
                AccountId TEXT NOT NULL,
                BannedByAccountId TEXT NOT NULL,
                BannedAt TEXT NOT NULL,
                Reason TEXT NULL,
                CONSTRAINT PK_CommunityBans PRIMARY KEY (CommunityId, AccountId),
                CONSTRAINT FK_CommunityBans_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE,
                CONSTRAINT FK_CommunityBans_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_CommunityBans_Accounts_BannedByAccountId FOREIGN KEY (BannedByAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS IX_CommunityBans_AccountId ON CommunityBans (AccountId);
            CREATE INDEX IF NOT EXISTS IX_CommunityBans_BannedByAccountId ON CommunityBans (BannedByAccountId);
            """);

        var communitiesWithDefaults = await db.CommunityRoles.Where(value => value.IsDefault)
            .Select(value => value.CommunityId).ToListAsync();
        var missing = await db.Communities.Where(value => !communitiesWithDefaults.Contains(value.Id))
            .Select(value => value.Id).ToListAsync();
        foreach (var communityId in missing)
            db.CommunityRoles.Add(new CommunityRole
            {
                Id = Guid.NewGuid(), CommunityId = communityId, Community = null!, Name = "@everyone",
                Position = 0, IsDefault = true,
                Permissions = CommunityPermission.ViewChannels | CommunityPermission.SendMessages |
                              CommunityPermission.ConnectVoice | CommunityPermission.SpeakVoice |
                              CommunityPermission.ShareScreen | CommunityPermission.ReadMessageHistory |
                              CommunityPermission.AttachFiles | CommunityPermission.EmbedLinks |
                              CommunityPermission.AddReactions
            });
        if (missing.Count > 0) await db.SaveChangesAsync();
    }

    public static async Task EnsureCommunityVoiceSchemaAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "CommunityChannels", "Kind", "INTEGER NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE CommunityRoles SET Permissions = Permissions | {0} WHERE IsDefault = 1",
            (long)(CommunityPermission.ConnectVoice | CommunityPermission.SpeakVoice |
                   CommunityPermission.ShareScreen | CommunityPermission.ReadMessageHistory |
                   CommunityPermission.AttachFiles | CommunityPermission.EmbedLinks |
                   CommunityPermission.AddReactions));
    }

    public static async Task EnsureCommunityPermissionOverwriteSchemaAsync(IridiumDbContext db)
    {
        var newOverwriteSchema = !await TableExistsAsync(db, "CommunityPermissionOverwrites");
        await EnsureColumnAsync(db, "CommunityChannels", "PermissionsSyncedToCategory", "INTEGER NOT NULL DEFAULT 0");
        if (newOverwriteSchema)
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE CommunityChannels SET PermissionsSyncedToCategory = 1 WHERE CategoryId IS NOT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CommunityPermissionOverwrites (
                Id TEXT NOT NULL CONSTRAINT PK_CommunityPermissionOverwrites PRIMARY KEY,
                CommunityId TEXT NOT NULL,
                ScopeType INTEGER NOT NULL,
                ScopeId TEXT NOT NULL,
                TargetType INTEGER NOT NULL,
                TargetId TEXT NULL,
                Allow INTEGER NOT NULL,
                Deny INTEGER NOT NULL,
                CONSTRAINT FK_CommunityPermissionOverwrites_Communities_CommunityId
                    FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunityPermissionOverwrites_ScopeTarget
                ON CommunityPermissionOverwrites (CommunityId, ScopeType, ScopeId, TargetType, TargetId);
            CREATE INDEX IF NOT EXISTS IX_CommunityPermissionOverwrites_Scope
                ON CommunityPermissionOverwrites (CommunityId, ScopeType, ScopeId);
            """);
    }

    private static async Task<bool> ColumnExistsAsync(IridiumDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<bool> TableExistsAsync(IridiumDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    public static async Task EnsureDirectMessageTablesAsync(IridiumDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS DirectConversations (
                Id TEXT NOT NULL CONSTRAINT PK_DirectConversations PRIMARY KEY,
                ParticipantAAccountId TEXT NOT NULL,
                ParticipantBAccountId TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT FK_DirectConversations_Accounts_ParticipantAAccountId FOREIGN KEY (ParticipantAAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_DirectConversations_Accounts_ParticipantBAccountId FOREIGN KEY (ParticipantBAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DirectConversations_ParticipantAAccountId_ParticipantBAccountId
                ON DirectConversations (ParticipantAAccountId, ParticipantBAccountId);
            CREATE TABLE IF NOT EXISTS DirectConversationStates (
                ConversationId TEXT NOT NULL,
                AccountId TEXT NOT NULL,
                HiddenAt INTEGER NULL,
                LastReadAt INTEGER NULL,
                CONSTRAINT PK_DirectConversationStates PRIMARY KEY (ConversationId, AccountId),
                CONSTRAINT FK_DirectConversationStates_DirectConversations_ConversationId FOREIGN KEY (ConversationId) REFERENCES DirectConversations (Id) ON DELETE CASCADE,
                CONSTRAINT FK_DirectConversationStates_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts (Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS DirectMessages (
                Id TEXT NOT NULL CONSTRAINT PK_DirectMessages PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                AuthorAccountId TEXT NOT NULL,
                Kind INTEGER NOT NULL DEFAULT 0,
                RelatedCallId TEXT NULL,
                Content TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                EditedAt INTEGER NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                DeletedAt INTEGER NULL,
                ReplyToMessageId TEXT NULL,
                CONSTRAINT FK_DirectMessages_DirectConversations_ConversationId FOREIGN KEY (ConversationId) REFERENCES DirectConversations (Id) ON DELETE CASCADE,
                CONSTRAINT FK_DirectMessages_Accounts_AuthorAccountId FOREIGN KEY (AuthorAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_DirectMessages_DirectMessages_ReplyToMessageId FOREIGN KEY (ReplyToMessageId) REFERENCES DirectMessages (Id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS IX_DirectMessages_ConversationId_CreatedAt ON DirectMessages (ConversationId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_DirectMessages_AuthorAccountId ON DirectMessages (AuthorAccountId);
            CREATE INDEX IF NOT EXISTS IX_DirectMessages_ReplyToMessageId ON DirectMessages (ReplyToMessageId);
            """);
        await EnsureColumnAsync(db, "DirectConversationStates", "LastReadAt", "INTEGER NULL");
        await EnsureColumnAsync(db, "DirectMessages", "Kind", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "DirectMessages", "RelatedCallId", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DirectMessages_RelatedCallId_Kind
                ON DirectMessages (RelatedCallId, Kind) WHERE RelatedCallId IS NOT NULL;
            """);
    }

    public static Task EnsurePresenceColumnAsync(IridiumDbContext db) =>
        EnsureColumnAsync(db, "Accounts", "PreferredPresence", "INTEGER NOT NULL DEFAULT 0");

    public static Task EnsureCommunityChannelReadStatesAsync(IridiumDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CommunityChannelReadStates (
                CommunityId TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                AccountId TEXT NOT NULL,
                LastReadAt INTEGER NOT NULL,
                CONSTRAINT PK_CommunityChannelReadStates PRIMARY KEY (CommunityId, ChannelId, AccountId),
                CONSTRAINT FK_CommunityChannelReadStates_CommunityChannels_CommunityId_ChannelId
                    FOREIGN KEY (CommunityId, ChannelId) REFERENCES CommunityChannels (CommunityId, Id) ON DELETE CASCADE,
                CONSTRAINT FK_CommunityChannelReadStates_Accounts_AccountId
                    FOREIGN KEY (AccountId) REFERENCES Accounts (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_CommunityChannelReadStates_AccountId_CommunityId
                ON CommunityChannelReadStates (AccountId, CommunityId);
            """);

    public static Task EnsureAccountBlocksAsync(IridiumDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AccountBlocks (
                BlockingAccountId TEXT NOT NULL,
                BlockedAccountId TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT PK_AccountBlocks PRIMARY KEY (BlockingAccountId, BlockedAccountId),
                CONSTRAINT FK_AccountBlocks_Accounts_BlockingAccountId FOREIGN KEY (BlockingAccountId) REFERENCES Accounts (Id) ON DELETE CASCADE,
                CONSTRAINT FK_AccountBlocks_Accounts_BlockedAccountId FOREIGN KEY (BlockedAccountId) REFERENCES Accounts (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_AccountBlocks_BlockedAccountId ON AccountBlocks (BlockedAccountId);
            """);

    public static Task EnsureCommunityMentionNotificationsAsync(IridiumDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CommunityMentionNotifications (
                MessageId TEXT NOT NULL,
                AccountId TEXT NOT NULL,
                CommunityId TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                ReadAt INTEGER NULL,
                CONSTRAINT PK_CommunityMentionNotifications PRIMARY KEY (MessageId, AccountId),
                CONSTRAINT FK_CommunityMentionNotifications_ChannelMessages_MessageId FOREIGN KEY (MessageId) REFERENCES ChannelMessages (Id) ON DELETE CASCADE,
                CONSTRAINT FK_CommunityMentionNotifications_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_CommunityMentionNotifications_AccountId_CommunityId_ChannelId_ReadAt
                ON CommunityMentionNotifications (AccountId, CommunityId, ChannelId, ReadAt);
            """);

    public static Task EnsureMessageHistoryIndexesAsync(IridiumDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_ChannelMessages_CommunityId_ChannelId_CreatedAt_Id
                ON ChannelMessages (CommunityId, ChannelId, CreatedAt, Id);
            CREATE INDEX IF NOT EXISTS IX_DirectMessages_ConversationId_CreatedAt_Id
                ON DirectMessages (ConversationId, CreatedAt, Id);
            """);

    public static async Task EnsureMessageClientIdsAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "ChannelMessages", "ClientMessageId", "TEXT NULL");
        await EnsureColumnAsync(db, "DirectMessages", "ClientMessageId", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ChannelMessages_AuthorAccountId_CommunityId_ChannelId_ClientMessageId
                ON ChannelMessages (AuthorAccountId, CommunityId, ChannelId, ClientMessageId)
                WHERE ClientMessageId IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DirectMessages_AuthorAccountId_ConversationId_ClientMessageId
                ON DirectMessages (AuthorAccountId, ConversationId, ClientMessageId)
                WHERE ClientMessageId IS NOT NULL;
            """);
    }

    private static async Task EnsureColumnAsync(IridiumDbContext db, string table, string column, string definition)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info('{table}');";
        await using (var reader = await inspect.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync();
    }

    public static async Task EnsureAccountSessionActivityColumnAsync(IridiumDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info('AccountSessions');";
        var hasLastUsedAt = false;
        await using (var reader = await inspect.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (!string.Equals(reader.GetString(1), "LastUsedAt", StringComparison.OrdinalIgnoreCase)) continue;
                hasLastUsedAt = true;
                break;
            }
        }

        if (hasLastUsedAt) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE AccountSessions ADD COLUMN LastUsedAt TEXT NULL;";
        await alter.ExecuteNonQueryAsync();
        await using var initialize = connection.CreateCommand();
        initialize.CommandText = "UPDATE AccountSessions SET LastUsedAt = CreatedAt WHERE LastUsedAt IS NULL;";
        await initialize.ExecuteNonQueryAsync();
    }

    public static async Task EnsurePronounsColumnAsync(IridiumDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info('Accounts');";
        var hasPronouns = false;
        await using (var reader = await inspect.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "Pronouns", StringComparison.OrdinalIgnoreCase))
                {
                    hasPronouns = true;
                    break;
                }
            }
        }

        if (hasPronouns) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Accounts ADD COLUMN Pronouns TEXT NULL;";
        await alter.ExecuteNonQueryAsync();
    }

    public static async Task EnsureFriendsTableAsync(IridiumDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Friendships (
                Id TEXT NOT NULL CONSTRAINT PK_Friendships PRIMARY KEY,
                RequesterAccountId TEXT NOT NULL,
                AddresseeAccountId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                AcceptedAt TEXT NULL,
                CONSTRAINT FK_Friendships_Accounts_RequesterAccountId FOREIGN KEY (RequesterAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_Friendships_Accounts_AddresseeAccountId FOREIGN KEY (AddresseeAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Friendships_RequesterAccountId_AddresseeAccountId
                ON Friendships (RequesterAccountId, AddresseeAccountId);
            CREATE INDEX IF NOT EXISTS IX_Friendships_AddresseeAccountId ON Friendships (AddresseeAccountId);
            """);
    }

    public static async Task EnsureCommunityStructureTablesAsync(IridiumDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CommunityCategories (
                CommunityId TEXT NOT NULL,
                Id TEXT NOT NULL,
                Name TEXT NOT NULL,
                Position INTEGER NOT NULL,
                ParentCategoryId TEXT NULL,
                CONSTRAINT PK_CommunityCategories PRIMARY KEY (CommunityId, Id),
                CONSTRAINT FK_CommunityCategories_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE,
                CONSTRAINT FK_CommunityCategories_CommunityCategories_CommunityId_ParentCategoryId
                    FOREIGN KEY (CommunityId, ParentCategoryId) REFERENCES CommunityCategories (CommunityId, Id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS IX_CommunityCategories_CommunityId_Position ON CommunityCategories (CommunityId, Position);
            CREATE TABLE IF NOT EXISTS CommunityChannels (
                CommunityId TEXT NOT NULL,
                Id TEXT NOT NULL,
                CategoryId TEXT NULL,
                Name TEXT NOT NULL,
                Kind INTEGER NOT NULL DEFAULT 0,
                PermissionsSyncedToCategory INTEGER NOT NULL DEFAULT 0,
                Position INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT PK_CommunityChannels PRIMARY KEY (CommunityId, Id),
                CONSTRAINT FK_CommunityChannels_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE,
                CONSTRAINT FK_CommunityChannels_CommunityCategories_CommunityId_CategoryId FOREIGN KEY (CommunityId, CategoryId) REFERENCES CommunityCategories (CommunityId, Id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS IX_CommunityChannels_CommunityId_CategoryId_Position ON CommunityChannels (CommunityId, CategoryId, Position);
            """);
        await EnsureColumnAsync(db, "CommunityCategories", "ParentCategoryId", "TEXT NULL");
        await EnsureColumnAsync(db, "CommunityChannels", "Kind", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "CommunityChannels", "PermissionsSyncedToCategory", "INTEGER NOT NULL DEFAULT 0");
        await EnsureCommunityChannelCategoryNullableAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_CommunityCategories_CommunityId_ParentCategoryId_Position
                ON CommunityCategories (CommunityId, ParentCategoryId, Position);
            """);
    }

    public static async Task EnsureUnifiedCommunitySidebarOrderingAsync(IridiumDbContext db)
    {
        await EnsureColumnAsync(db, "CommunityChannels", "Kind", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "CommunityChannels", "PermissionsSyncedToCategory", "INTEGER NOT NULL DEFAULT 0");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        // SQLite compares TEXT primary keys case-sensitively. Microsoft.Data.Sqlite writes
        // Guid values in upper-case, so normalize IDs created by the original raw-SQL
        // compatibility path before EF starts tracking them. Foreign-key checks are
        // deferred so references and principals can be updated in one transaction.
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;");
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE CommunityChannels
            SET CategoryId = upper(CategoryId)
            WHERE CategoryId IS NOT NULL AND CategoryId <> upper(CategoryId);

            UPDATE CommunityCategories
            SET ParentCategoryId = upper(ParentCategoryId)
            WHERE ParentCategoryId IS NOT NULL AND ParentCategoryId <> upper(ParentCategoryId);

            UPDATE CommunityCategories
            SET Id = upper(Id)
            WHERE Id <> upper(Id);

            """);

        // The SQL above may have changed key values represented by previously tracked
        // entities when this idempotent compatibility method is exercised more than once.
        db.ChangeTracker.Clear();
        var communityIds = await db.Communities.Select(value => value.Id).ToListAsync();
        foreach (var communityId in communityIds)
        {
            var categories = await db.CommunityCategories
                .Where(value => value.CommunityId == communityId).ToListAsync();
            var channels = await db.CommunityChannels.Where(value => value.CommunityId == communityId).ToListAsync();
            foreach (var parentId in categories.Select(value => (Guid?)value.Id).Append(null))
            {
                var items = categories.Where(value => value.ParentCategoryId == parentId)
                    .Select(value => new CompatibilitySidebarItem(value.Position, 0, value.Name, value.Id,
                        position => value.Position = position))
                    .Concat(channels.Where(value => value.CategoryId == parentId)
                        .Select(value => new CompatibilitySidebarItem(value.Position, 1, value.Name, value.Id,
                            position => value.Position = position)))
                    .OrderBy(value => value.Position).ThenBy(value => value.Kind)
                    .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Id).ToList();
                for (var index = 0; index < items.Count; index++) items[index].SetPosition(index);
            }
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task EnsureCommunityChannelCategoryNullableAsync(IridiumDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        var categoryIsRequired = false;
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info('CommunityChannels');";
            await using var reader = await inspect.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (string.Equals(reader.GetString(1), "CategoryId", StringComparison.OrdinalIgnoreCase))
                    categoryIsRequired = reader.GetInt32(3) != 0;
        }
        if (!categoryIsRequired) return;

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await db.Database.ExecuteSqlRawAsync("""
                DROP TABLE IF EXISTS CommunityChannels_NullableUpgrade;
                CREATE TABLE CommunityChannels_NullableUpgrade (
                    CommunityId TEXT NOT NULL,
                    Id TEXT NOT NULL,
                    CategoryId TEXT NULL,
                    Name TEXT NOT NULL,
                    Kind INTEGER NOT NULL DEFAULT 0,
                    PermissionsSyncedToCategory INTEGER NOT NULL DEFAULT 0,
                    Position INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CONSTRAINT PK_CommunityChannels PRIMARY KEY (CommunityId, Id),
                    CONSTRAINT FK_CommunityChannels_Communities_CommunityId FOREIGN KEY (CommunityId) REFERENCES Communities (Id) ON DELETE CASCADE,
                    CONSTRAINT FK_CommunityChannels_CommunityCategories_CommunityId_CategoryId FOREIGN KEY (CommunityId, CategoryId) REFERENCES CommunityCategories (CommunityId, Id) ON DELETE RESTRICT
                );
                INSERT INTO CommunityChannels_NullableUpgrade (CommunityId, Id, CategoryId, Name, Kind, PermissionsSyncedToCategory, Position, CreatedAt)
                    SELECT CommunityId, Id, CategoryId, Name, Kind, PermissionsSyncedToCategory, Position, CreatedAt FROM CommunityChannels;
                DROP TABLE CommunityChannels;
                ALTER TABLE CommunityChannels_NullableUpgrade RENAME TO CommunityChannels;
                CREATE INDEX IX_CommunityChannels_CommunityId_CategoryId_Position
                    ON CommunityChannels (CommunityId, CategoryId, Position);
                """);
            await transaction.CommitAsync();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            db.ChangeTracker.Clear();
        }
    }

    private sealed record CompatibilitySidebarItem(
        int Position, int Kind, string Name, Guid Id, Action<int> SetPosition);

    public static async Task EnsureChannelMessagesTableAsync(IridiumDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ChannelMessages (
                Id TEXT NOT NULL CONSTRAINT PK_ChannelMessages PRIMARY KEY,
                CommunityId TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                AuthorAccountId TEXT NOT NULL,
                Content TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                EditedAt INTEGER NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                DeletedAt INTEGER NULL,
                ReplyToMessageId TEXT NULL,
                CONSTRAINT FK_ChannelMessages_CommunityChannels_CommunityId_ChannelId
                    FOREIGN KEY (CommunityId, ChannelId) REFERENCES CommunityChannels (CommunityId, Id) ON DELETE CASCADE,
                CONSTRAINT FK_ChannelMessages_Accounts_AuthorAccountId
                    FOREIGN KEY (AuthorAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_ChannelMessages_ChannelMessages_ReplyToMessageId
                    FOREIGN KEY (ReplyToMessageId) REFERENCES ChannelMessages (Id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ChannelMessages_CommunityId_ChannelId_CreatedAt
                ON ChannelMessages (CommunityId, ChannelId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_ChannelMessages_AuthorAccountId ON ChannelMessages (AuthorAccountId);
            CREATE INDEX IF NOT EXISTS IX_ChannelMessages_ReplyToMessageId ON ChannelMessages (ReplyToMessageId);
            """);
        await EnsureColumnAsync(db, "ChannelMessages", "MentionsJson", "TEXT NULL");
    }

    public static async Task EnsureAttachmentsTableAsync(IridiumDbContext db)
    {
        // Keep table creation separate from index creation. CREATE TABLE IF NOT EXISTS does not
        // add columns to a legacy table, so every additive column must be ensured first.
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS Attachments (
            Id TEXT NOT NULL CONSTRAINT PK_Attachments PRIMARY KEY,
            UploaderAccountId TEXT NOT NULL,
            ChannelMessageId TEXT NULL,
            DirectMessageId TEXT NULL,
            OriginalFileName TEXT NOT NULL,
            StoredObjectKey TEXT NOT NULL,
            ContentType TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL,
            CreatedAt INTEGER NOT NULL,
            Width INTEGER NULL,
            Height INTEGER NULL,
            IsSpoiler INTEGER NOT NULL DEFAULT 0,
            AverageColor TEXT NULL,
            PreviewObjectKey TEXT NULL,
            PreviewContentType TEXT NULL,
            PreviewSizeBytes INTEGER NULL,
            CONSTRAINT FK_Attachments_Accounts_UploaderAccountId FOREIGN KEY (UploaderAccountId) REFERENCES Accounts (Id) ON DELETE RESTRICT,
            CONSTRAINT FK_Attachments_ChannelMessages_ChannelMessageId FOREIGN KEY (ChannelMessageId) REFERENCES ChannelMessages (Id) ON DELETE CASCADE,
            CONSTRAINT FK_Attachments_DirectMessages_DirectMessageId FOREIGN KEY (DirectMessageId) REFERENCES DirectMessages (Id) ON DELETE CASCADE
        );
        """);

        // The original object key/type/size remain mapped to the legacy physical columns
        // StoredObjectKey, ContentType, and SizeBytes. Preview data is absent for legacy rows.
        await EnsureColumnAsync(db, "Attachments", "Width", "INTEGER NULL");
        await EnsureColumnAsync(db, "Attachments", "Height", "INTEGER NULL");
        await EnsureColumnAsync(db, "Attachments", "IsSpoiler", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "Attachments", "AverageColor", "TEXT NULL");
        await EnsureColumnAsync(db, "Attachments", "PreviewObjectKey", "TEXT NULL");
        await EnsureColumnAsync(db, "Attachments", "PreviewContentType", "TEXT NULL");
        await EnsureColumnAsync(db, "Attachments", "PreviewSizeBytes", "INTEGER NULL");

        await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Attachments_StoredObjectKey ON Attachments (StoredObjectKey);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Attachments_PreviewObjectKey
            ON Attachments (PreviewObjectKey) WHERE PreviewObjectKey IS NOT NULL;
        CREATE INDEX IF NOT EXISTS IX_Attachments_ChannelMessageId ON Attachments (ChannelMessageId);
        CREATE INDEX IF NOT EXISTS IX_Attachments_DirectMessageId ON Attachments (DirectMessageId);
        CREATE INDEX IF NOT EXISTS IX_Attachments_UploaderAccountId ON Attachments (UploaderAccountId);
        """);
    }
}
