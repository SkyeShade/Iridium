const databaseName = "IridiumClientCache";
const schemaVersion = 2;
const recentMessageLimit = 300;
const initialWindowLimit = 50;
const globalMessageLimit = 10000;
const inactiveMaximumAgeMs = 120 * 24 * 60 * 60 * 1000;

let databasePromise;

function request(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function transactionDone(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error || new Error("IndexedDB transaction aborted."));
    });
}

function openDatabase() {
    databasePromise ??= new Promise((resolve, reject) => {
        const opening = indexedDB.open(databaseName, schemaVersion);
        opening.onupgradeneeded = event => {
            const db = opening.result;
            const store = name => db.objectStoreNames.contains(name)
                ? opening.transaction.objectStore(name)
                : db.createObjectStore(name, { keyPath: "key" });
            const index = (target, name) => {
                if (!target.indexNames.contains(name)) target.createIndex(name, name, { unique: false });
            };
            const messages = store("messages");
            for (const name of ["conversationKey", "accountKey", "nodeKey", "lastAccess"]) index(messages, name);
            const conversations = store("conversations");
            for (const name of ["accountKey", "nodeKey", "lastAccess"]) index(conversations, name);
            const media = store("media");
            for (const name of ["conversationKey", "accountKey", "nodeKey", "lastAccess"]) index(media, name);
            // Version 1 payloads may contain a Community-profile projection for legacy
            // messages. That projection cannot be distinguished from an account default,
            // so discard it once and let canonical history repopulate the cache.
            if (event.oldVersion > 0 && event.oldVersion < 2) {
                messages.clear();
                conversations.clear();
                media.clear();
            }
        };
        opening.onsuccess = () => resolve(opening.result);
        opening.onerror = () => reject(opening.error);
        opening.onblocked = () => reject(new Error("Iridium message-cache schema upgrade is blocked by another tab."));
    });
    return databasePromise;
}

function accountKey(scope) {
    return scope.accountKey;
}

function recordFor(scope, message, now) {
    const messageId = String(message.id).toLowerCase();
    return {
        key: `${scope.conversationKey}|message:${messageId}`,
        conversationKey: scope.conversationKey,
        accountKey: accountKey(scope),
        nodeKey: scope.nodeKey,
        kind: scope.kind,
        messageId,
        createdAt: new Date(message.createdAt).getTime(),
        lastAccess: now,
        payload: message
    };
}

function cacheable(message) {
    return message && !message.isDeleted && (message.deliveryState === undefined || message.deliveryState === 0);
}

function attachmentRecords(scope, message, now) {
    return (message.attachments || []).map(attachment => ({
        key: `${scope.conversationKey}|media:${String(attachment.id).toLowerCase()}`,
        conversationKey: scope.conversationKey,
        accountKey: accountKey(scope),
        nodeKey: scope.nodeKey,
        lastAccess: now,
        messageId: String(message.id).toLowerCase(),
        metadata: attachment
    }));
}

async function recordsForConversation(store, conversationKey) {
    return await request(store.index("conversationKey").getAll(conversationKey));
}

export async function getRecent(scope) {
    const db = await openDatabase();
    const tx = db.transaction(["messages", "conversations"], "readwrite");
    const records = await recordsForConversation(tx.objectStore("messages"), scope.conversationKey);
    const metadata = await request(tx.objectStore("conversations").get(scope.conversationKey));
    if (!records.length) { await transactionDone(tx); return null; }
    const now = Date.now();
    tx.objectStore("conversations").put({ ...(metadata || {}), key: scope.conversationKey,
        accountKey: accountKey(scope), nodeKey: scope.nodeKey, kind: scope.kind,
        conversationId: scope.conversationId, lastAccess: now,
        olderCursor: metadata?.olderCursor ?? null, hasOlder: metadata?.hasOlder ?? false });
    await transactionDone(tx);
    const messages = records.filter(value => cacheable(value.payload))
        .sort((left, right) => left.createdAt - right.createdAt || left.messageId.localeCompare(right.messageId))
        // Only surface the same latest window the server will immediately validate. Older cached pages remain
        // available in the bounded store without allowing an unvalidated old deletion to reappear after refresh.
        .slice(-initialWindowLimit).map(value => value.payload);
    if (!messages.length) return null;
    return { messages, olderCursor: metadata?.olderCursor ?? null, hasOlder: metadata?.hasOlder ?? false,
        isAroundWindow: false, targetMessageId: null };
}

export async function reconcileRecent(scope, page) {
    const db = await openDatabase();
    const tx = db.transaction(["messages", "conversations", "media"], "readwrite");
    const messageStore = tx.objectStore("messages");
    const mediaStore = tx.objectStore("media");
    const existing = await recordsForConversation(messageStore, scope.conversationKey);
    const incoming = (page.messages || []).filter(cacheable);
    const incomingIds = new Set(incoming.map(value => String(value.id).toLowerCase()));
    const oldest = incoming.length ? Math.min(...incoming.map(value => new Date(value.createdAt).getTime())) : null;
    const removedIds = new Set();
    for (const record of existing) {
        if (oldest === null || (record.createdAt >= oldest && !incomingIds.has(record.messageId))) {
            messageStore.delete(record.key);
            removedIds.add(record.messageId);
        }
    }
    for (const media of await recordsForConversation(mediaStore, scope.conversationKey))
        if (removedIds.has(media.messageId)) mediaStore.delete(media.key);
    const now = Date.now();
    for (const message of incoming) {
        messageStore.put(recordFor(scope, message, now));
        for (const media of attachmentRecords(scope, message, now)) mediaStore.put(media);
    }
    tx.objectStore("conversations").put({ key: scope.conversationKey, accountKey: accountKey(scope),
        nodeKey: scope.nodeKey, kind: scope.kind, conversationId: scope.conversationId,
        olderCursor: page.olderCursor ?? null, hasOlder: !!page.hasOlder, lastAccess: now });
    await trimConversation(messageStore, mediaStore, scope.conversationKey);
    await transactionDone(tx);
    void prune().catch(() => {});
}

export async function upsertMessages(scope, messages) {
    const cacheableMessages = (messages || []).filter(cacheable);
    if (!cacheableMessages.length) return;
    const db = await openDatabase();
    const tx = db.transaction(["messages", "conversations", "media"], "readwrite");
    const messageStore = tx.objectStore("messages");
    const mediaStore = tx.objectStore("media");
    const now = Date.now();
    for (const message of cacheableMessages) {
        messageStore.put(recordFor(scope, message, now));
        for (const media of attachmentRecords(scope, message, now)) mediaStore.put(media);
    }
    const conversationStore = tx.objectStore("conversations");
    const existing = await request(conversationStore.get(scope.conversationKey));
    conversationStore.put({ ...(existing || {}), key: scope.conversationKey, accountKey: accountKey(scope),
        nodeKey: scope.nodeKey, kind: scope.kind, conversationId: scope.conversationId, lastAccess: now });
    await trimConversation(messageStore, mediaStore, scope.conversationKey);
    await transactionDone(tx);
    void prune().catch(() => {});
}

async function trimConversation(messageStore, mediaStore, conversationKey) {
    const records = await recordsForConversation(messageStore, conversationKey);
    records.sort((left, right) => left.createdAt - right.createdAt || left.messageId.localeCompare(right.messageId));
    const removed = records.slice(0, Math.max(0, records.length - recentMessageLimit));
    const removedIds = new Set(removed.map(value => value.messageId));
    for (const record of removed) messageStore.delete(record.key);
    if (removedIds.size)
        for (const media of await recordsForConversation(mediaStore, conversationKey))
            if (removedIds.has(media.messageId)) mediaStore.delete(media.key);
}

export async function removeMessage(scope, messageId) {
    const db = await openDatabase();
    const tx = db.transaction(["messages", "media"], "readwrite");
    const messages = tx.objectStore("messages");
    const normalizedId = String(messageId).toLowerCase();
    messages.delete(`${scope.conversationKey}|message:${normalizedId}`);
    const records = await recordsForConversation(messages, scope.conversationKey);
    for (const record of records) {
        if (String(record.payload?.replyTo?.messageId).toLowerCase() !== normalizedId) continue;
        record.payload.replyTo = { ...record.payload.replyTo, excerpt: null, isDeleted: true };
        messages.put(record);
    }
    const media = tx.objectStore("media");
    for (const record of await recordsForConversation(media, scope.conversationKey))
        if (record.messageId === normalizedId) media.delete(record.key);
    await transactionDone(tx);
}

async function clearByIndex(indexName, value) {
    const db = await openDatabase();
    const tx = db.transaction(["messages", "conversations", "media"], "readwrite");
    for (const storeName of ["messages", "conversations", "media"]) {
        const store = tx.objectStore(storeName);
        if (indexName === "conversationKey" && storeName === "conversations") {
            store.delete(value);
            continue;
        }
        for (const record of await request(store.index(indexName).getAll(value))) store.delete(record.key);
    }
    await transactionDone(tx);
}

export async function clearConversation(scope) { await clearByIndex("conversationKey", scope.conversationKey); }
export async function clearAccount(nodeKey, accountId) {
    await clearByIndex("accountKey", `${nodeKey}|account:${String(accountId).replaceAll("-", "").toLowerCase()}`);
}
export async function clearNode(nodeKey) { await clearByIndex("nodeKey", nodeKey); }

export async function clearCommunity(nodeKey, accountId, communityId) {
    const prefix = `${nodeKey}|account:${String(accountId).replaceAll("-", "").toLowerCase()}`;
    const target = String(communityId).toLowerCase();
    const db = await openDatabase();
    const read = db.transaction("messages", "readonly");
    const records = await request(read.objectStore("messages").index("accountKey").getAll(prefix));
    await transactionDone(read);
    const conversations = new Set(records.filter(record => String(record.payload?.communityId).toLowerCase() === target)
        .map(record => record.conversationKey));
    for (const conversationKey of conversations)
        await clearConversation({ conversationKey });
}

export async function prune() {
    const db = await openDatabase();
    const tx = db.transaction(["messages", "conversations", "media"], "readwrite");
    const messages = tx.objectStore("messages");
    const conversations = tx.objectStore("conversations");
    const media = tx.objectStore("media");
    const metadata = await request(conversations.getAll());
    const cutoff = Date.now() - inactiveMaximumAgeMs;
    const expired = new Set(metadata.filter(value => value.lastAccess < cutoff).map(value => value.key));
    let records = await request(messages.getAll());
    if (records.length > globalMessageLimit) {
        const byAge = [...metadata].sort((left, right) => left.lastAccess - right.lastAccess);
        let remaining = records.length;
        for (const conversation of byAge) {
            if (remaining <= globalMessageLimit) break;
            expired.add(conversation.key);
            remaining -= records.filter(value => value.conversationKey === conversation.key).length;
        }
    }
    for (const record of records) if (expired.has(record.conversationKey)) messages.delete(record.key);
    for (const record of await request(media.getAll())) if (expired.has(record.conversationKey)) media.delete(record.key);
    for (const key of expired) conversations.delete(key);
    await transactionDone(tx);
}
