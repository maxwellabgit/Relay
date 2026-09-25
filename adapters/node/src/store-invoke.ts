import type {
  CandidateEvent,
  CandidateEventStatus,
  CaseKind,
  CaseOrigin,
  CasePhase,
  CaseStatus,
  FeedItemRecord,
  JudgmentRecord,
} from "@relay/contracts";
import type {
  CandidateRecord,
  EpisodeRecord,
  MemoryKind,
  MemoryRecord,
  PatternEvidenceRecord,
  PatternRecord,
  PersistedSourceEvent,
  ReceiptRecord,
  ReviewRecord,
  WorkItem,
  WorkSessionRecord,
} from "@relay/engine";
import type { StoreInvoke } from "@relay/adapter-tauri/engine-store";
import type { SqliteEngineStore } from "./sqlite-store.js";

export function sqliteStoreInvoke(store: SqliteEngineStore): StoreInvoke {
  return async (command, args) => {
    if (command !== "store_execute") throw new Error(`unknown_command:${command}`);
    const op = JSON.parse(JSON.stringify(args.op)) as Record<string, unknown>;
    const value = await dispatch(store, op);
    if (value === undefined || value === null) return null;
    return JSON.parse(JSON.stringify(value)) as unknown;
  };
}

async function dispatch(store: SqliteEngineStore, op: Record<string, unknown>): Promise<unknown> {
  switch (op.op) {
    case "ensure_session":
      await store.ensureSession(str(op, "sessionId"), str(op, "createdAt"));
      return null;
    case "set_listening":
      await store.setListening(str(op, "sessionId"), op.listening === true);
      return null;
    case "get_listening":
      return store.getListening(str(op, "sessionId"));
    case "get_hosted_processing":
      return store.getHostedProcessingEnabled();
    case "set_hosted_processing":
      await store.setHostedProcessingEnabled(op.enabled === true);
      return null;
    case "persist_final_source":
      return store.persistFinalSource(op.event as PersistedSourceEvent);
    case "create_case":
      return store.createCase({
        caseId: str(op, "caseId"),
        origin: str(op, "origin") as CaseOrigin,
        kind: str(op, "kind") as CaseKind,
        priority: Number(op.priority),
        at: str(op, "at"),
        ...(typeof op.parentCaseId === "string" ? { parentCaseId: op.parentCaseId } : {}),
      });
    case "get_case":
      return store.getCase(str(op, "caseId"));
    case "update_case":
      return store.updateCase(str(op, "caseId"), Number(op.expectedVersion), op.patch as {
        status?: CaseStatus;
        phase?: CasePhase;
        waitKind?: string | null;
        at: string;
      });
    case "append_case_event":
      await store.appendCaseEvent(
        str(op, "caseId"),
        Number(op.caseVersion),
        str(op, "type"),
        str(op, "at"),
        (op.payload ?? {}) as Record<string, unknown>,
      );
      return null;
    case "list_active_cases":
      return store.listActiveCases();
    case "list_feed_items":
      return store.listFeedItemRecords();
    case "add_feed_item":
      await store.addFeedItem(op.item as FeedItemRecord);
      return null;
    case "list_source_segments":
      return store.listSourceSegments(str(op, "sessionId"));
    case "enqueue":
      await store.enqueue(op.item as WorkItem);
      return null;
    case "claim_next":
      return store.claimNext(str(op, "now"), str(op, "owner"), Number(op.leaseMs));
    case "complete":
      await store.complete(str(op, "workId"));
      return null;
    case "requeue":
      await store.requeue(
        str(op, "workId"),
        str(op, "availableAt"),
        op.payload ? (op.payload as Record<string, unknown>) : undefined,
      );
      return null;
    case "upsert_judgment":
      await store.upsertJudgment(op.record as JudgmentRecord);
      return null;
    case "find_completed_judgment":
      return store.findCompletedJudgmentByHash(str(op, "requestHash"));
    case "append_domain_event":
      return store.appendDomainEvent(str(op, "type"), str(op, "at"), (op.payload ?? {}) as Record<string, unknown>);
    case "list_domain_events":
      return store.listDomainEvents(Number(op.limit));
    case "count_work_items":
      return store.countWorkItems();
    case "dead_letter":
      await store.deadLetter(str(op, "workId"), str(op, "reasonCode"), str(op, "at"));
      return null;
    case "list_dead_letters":
      return store.listDeadLetters();
    case "upsert_judgment_attempt":
      await store.upsertJudgmentAttempt(op.record as {
        attemptId: string;
        caseId: string;
        workId?: string;
        attempt: number;
        maxAttempts: number;
        nextAttemptAt?: string | null;
        failureCategory?: string | null;
        providerRequestId?: string | null;
        createdAt: string;
      });
      return null;
    case "put_memory":
      await store.learning.putMemory(op.record as MemoryRecord);
      return null;
    case "delete_memory":
      await store.learning.deleteMemory(str(op, "kind") as MemoryKind, str(op, "key"));
      return null;
    case "get_memory":
      return store.learning.getMemory(str(op, "kind") as MemoryKind, str(op, "key"));
    case "list_memories":
      return store.learning.listMemories();
    case "open_session":
      await store.learning.openSession(op.record as WorkSessionRecord);
      return null;
    case "close_session":
      await store.learning.closeSession(
        str(op, "sessionId"),
        str(op, "endedAt"),
        str(op, "termination") as "completed" | "abandoned",
        Number(op.episodeCount),
      );
      return null;
    case "current_session":
      return store.learning.currentSession();
    case "list_sessions":
      return store.learning.listSessions();
    case "put_episode":
      await store.learning.putEpisode(op.record as EpisodeRecord);
      return null;
    case "record_completed_episode":
      return store.learning.recordCompletedEpisode(op.record as EpisodeRecord);
    case "list_episodes":
      return store.learning.listEpisodes();
    case "put_receipt":
      await store.learning.putReceipt(op.record as ReceiptRecord);
      return null;
    case "list_receipts":
      return store.learning.listReceipts();
    case "put_pattern":
      await store.learning.putPattern(op.record as PatternRecord);
      return null;
    case "get_pattern":
      return store.learning.getPattern(str(op, "signature"));
    case "list_patterns":
      return store.learning.listPatterns();
    case "put_candidate":
      await store.learning.putCandidate(op.record as CandidateRecord);
      return null;
    case "list_candidates":
      return store.learning.listCandidates();
    case "put_pattern_evidence":
      await store.learning.putPatternEvidence(op.record as PatternEvidenceRecord);
      return null;
    case "list_pattern_evidence":
      return store.learning.listPatternEvidence(
        typeof op.signature === "string" ? op.signature : undefined,
      );
    case "put_review":
      await store.learning.putReview(op.record as ReviewRecord);
      return null;
    case "list_reviews":
      return store.learning.listReviews();
    case "compact":
      return store.learning.compact(str(op, "nowIso"));
    case "begin_transaction":
      await store.beginTransaction();
      return null;
    case "commit_transaction":
      await store.commitTransaction();
      return null;
    case "rollback_transaction":
      await store.rollbackTransaction();
      return null;
    case "put_candidate_event":
      await store.putCandidateEvent(op.event as CandidateEvent);
      return null;
    case "get_candidate_event":
      return store.getCandidateEvent(str(op, "candidateEventId"));
    case "list_candidate_events":
      return store.listCandidateEvents(typeof op.caseId === "string" ? op.caseId : undefined);
    case "update_candidate_event_status":
      await store.updateCandidateEventStatus(
        str(op, "candidateEventId"),
        str(op, "status") as CandidateEventStatus,
        str(op, "updatedAt"),
      );
      return null;
    case "put_ambient_suppression":
      await store.putAmbientSuppression(str(op, "key"), str(op, "reason"), str(op, "createdAt"));
      return null;
    case "is_ambient_suppressed":
      return store.isAmbientSuppressed(str(op, "key"));
    case "save_hosted_grant":
      await store.saveHostedGrant(op.grant as Parameters<SqliteEngineStore["saveHostedGrant"]>[0], str(op, "at"));
      return null;
    case "revoke_hosted_grant":
      await store.revokeHostedGrant(str(op, "grantId"), str(op, "at"));
      return null;
    case "find_hosted_grant":
      return store.findHostedGrant(str(op, "grantId"));
    case "read_hosted_grant":
      return store.readHostedGrant(op.scope as { kind: "session" | "project"; id: string });
    case "reserve_hosted_grant":
      return store.reserveHostedGrant({
        grantId: str(op, "grantId"),
        bytes: Number(op.bytes),
        now: str(op, "now"),
        reservationId: str(op, "reservationId"),
      });
    case "commit_hosted_grant":
      await store.commitHostedGrant(str(op, "reservationId"));
      return null;
    case "release_hosted_grant":
      await store.releaseHostedGrant(str(op, "reservationId"));
      return null;
    case "release_uncommitted_hosted_grants":
      return store.releaseUncommittedHostedGrants();
    case "foundation_put":
      await store.putFoundation(str(op, "kind"), str(op, "id"), Number(op.version), op.payload, str(op, "at"));
      return null;
    case "foundation_get":
      return store.getFoundation(str(op, "kind"), str(op, "id"));
    case "foundation_list":
      return store.listFoundation(str(op, "kind"));
    case "foundation_claim":
      return store.claimFoundation(str(op, "kind"), str(op, "id"), Number(op.version), op.payload, str(op, "at"));
    case "foundation_cas":
      return store.replaceFoundation(
        str(op, "kind"),
        str(op, "id"),
        Number(op.expectedVersion),
        Number(op.version),
        op.payload,
        str(op, "at"),
      );
    case "link_execution_case":
      await store.linkExecutionCase(str(op, "executionId"), str(op, "projectCaseId"), str(op, "at"));
      return null;
    case "list_execution_cases":
      return store.listExecutionCases(str(op, "executionId"));
    default:
      throw new Error(`unknown_op:${String(op.op)}`);
  }
}

function str(op: Record<string, unknown>, key: string): string {
  const value = op[key];
  if (typeof value !== "string") throw new Error(`missing_${key}`);
  return value;
}
