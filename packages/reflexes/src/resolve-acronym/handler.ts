import type {
  DetectionContext,
  JudgmentAnswer,
  ReflexContext,
  ReflexModule,
  ReflexResult,
  SourceEvent,
  TriggerCandidate,
} from "@relay/contracts";
import { resolveAcronymDefinition } from "./definition.js";
import { detectAcronymTokens } from "./detector.js";
import { buildAcronymQuestions } from "./questions.js";
import { bundledDictionaryLookup } from "./glossary-lookup.js";
import policy from "./policy.v1.json" with { type: "json" };

export type AcronymPolicyV1 = {
  readonly schemaVersion: 1;
  readonly choiceProbabilityMinimum: number;
  readonly choiceMarginMinimum: number;
  readonly displayUsefulnessMinimum: number;
  readonly repeatCooldownMs: number;
};

export type GlossaryLookup = {
  exactUser(token: string): Promise<string | null>;
  exactProject(token: string): Promise<string | null>;
  exactBundled(token: string): Promise<string | null>;
  searchWindow(token: string, text: string): Promise<string[]>;
};

export type AcronymEvaluateExtras = {
  readonly glossary: GlossaryLookup;
  readonly isExplicitAsk: boolean;
  readonly recentlyShown: ReadonlySet<string>;
  readonly judgmentAnswers?: Readonly<Record<string, JudgmentAnswer>>;
};

const defaultGlossary: GlossaryLookup = {
  exactUser: async () => null,
  exactProject: async () => null,
  exactBundled: async (token) => bundledDictionaryLookup(token),
  searchWindow: async (token, text) => {
    const re = new RegExp(
      `${token}\\s+(?:means|stands for|=)\\s+([A-Za-z][A-Za-z\\s-]{2,80})`,
      "i",
    );
    const m = text.match(re);
    return m?.[1] ? [m[1].trim()] : [];
  },
};

export function applyAcronymPolicy(
  answers: Readonly<Record<string, JudgmentAnswer>>,
  opts: { isExplicitAsk: boolean; policy?: AcronymPolicyV1 },
): { show: boolean; expansion: string | null; reasonCode: string } {
  const p = opts.policy ?? (policy as AcronymPolicyV1);
  const choice = answers.expansion;
  const useful = answers.useful;
  if (!choice || choice.type !== "choice") {
    return { show: false, expansion: null, reasonCode: "missing_choice" };
  }
  if (choice.choice === "no_match") {
    return { show: false, expansion: null, reasonCode: "no_match" };
  }
  const probs = Object.entries(choice.probabilities).sort((a, b) => b[1]! - a[1]!);
  const top = probs[0];
  const second = probs[1];
  if (!top || top[1]! < p.choiceProbabilityMinimum) {
    return { show: false, expansion: null, reasonCode: "below_choice_minimum" };
  }
  if (second && top[1]! - second[1]! < p.choiceMarginMinimum) {
    return { show: false, expansion: null, reasonCode: "below_choice_margin" };
  }
  if (!opts.isExplicitAsk) {
    if (!useful || useful.type !== "noul" || useful.probabilityYes < p.displayUsefulnessMinimum) {
      return { show: false, expansion: choice.choice, reasonCode: "below_usefulness" };
    }
  }
  return { show: true, expansion: choice.choice, reasonCode: "policy_pass" };
}

export function createResolveAcronymModule(
  extras: Partial<AcronymEvaluateExtras> = {},
): ReflexModule {
  const glossary = extras.glossary ?? defaultGlossary;

  return {
    definition: resolveAcronymDefinition,
    detect(event: SourceEvent, context: DetectionContext): TriggerCandidate[] {
      const caseInsensitive = event.origin === "typed";
      void context;
      return detectAcronymTokens(event.text, { caseInsensitive }).map((t) => ({
        reflexId: resolveAcronymDefinition.id,
        reflexVersion: resolveAcronymDefinition.version,
        token: t.token,
        start: t.start,
        end: t.end,
        reason: "uppercase_token",
      }));
    },
    async evaluate(context: ReflexContext): Promise<ReflexResult> {
      const text = context.observationText ?? "";
      const token = context.triggerToken ?? "";
      const isExplicitAsk = extras.isExplicitAsk === true || context.isExplicitAsk === true;
      const recentlyShown = extras.recentlyShown ?? new Set<string>();

      if (!token) {
        return { type: "no_action", summary: "no_token", sourceRefs: [], judgmentIds: [] };
      }
      if (recentlyShown.has(token)) {
        return {
          type: "no_action",
          summary: "cooldown",
          sourceRefs: context.triggerSourceRefs,
          judgmentIds: [],
        };
      }

      const user = await glossary.exactUser(token);
      if (user) {
        return finding(token, user, context, "explicit_memory");
      }
      const project = await glossary.exactProject(token);
      if (project) {
        return finding(token, project, context, "exact_glossary");
      }
      const bundled = await glossary.exactBundled(token);
      const contextCandidates = await glossary.searchWindow(token, text);
      const candidates = new Set<string>();
      if (bundled) candidates.add(bundled);
      for (const c of contextCandidates) candidates.add(c);

      const list = [...candidates];
      if (list.length === 1) {
        return finding(token, list[0]!, context, bundled === list[0] ? "exact_glossary" : "context_candidate");
      }

      if (list.length === 0) {
        return {
          type: "no_action",
          summary: "no_candidates",
          sourceRefs: context.triggerSourceRefs,
          judgmentIds: [],
        };
      }

      void buildAcronymQuestions(list);
      if (extras.judgmentAnswers) {
        const decision = applyAcronymPolicy(extras.judgmentAnswers, { isExplicitAsk });
        if (!decision.show || !decision.expansion) {
          return {
            type: "no_action",
            summary: decision.reasonCode,
            sourceRefs: context.triggerSourceRefs,
            judgmentIds: [],
          };
        }
        return finding(token, decision.expansion, context, decision.reasonCode);
      }

      return {
        type: "clarification_required",
        summary: "judgment_required",
        sourceRefs: context.triggerSourceRefs,
        judgmentIds: [],
        clarificationPrompt: JSON.stringify({
          token,
          optionIds: list,
          policyVersion: "resolve-acronym@1",
          choiceProbabilityMinimum: (policy as AcronymPolicyV1).choiceProbabilityMinimum,
          choiceMarginMinimum: (policy as AcronymPolicyV1).choiceMarginMinimum,
          displayUsefulnessMinimum: (policy as AcronymPolicyV1).displayUsefulnessMinimum,
        }),
      };
    },
  };
}

function finding(
  token: string,
  expansion: string,
  context: ReflexContext,
  reasonCode: string,
): ReflexResult {
  void reasonCode;
  return {
    type: "finding",
    summary: `${token}: ${expansion}`,
    sourceRefs: context.triggerSourceRefs,
    judgmentIds: [],
    evidenceDrafts: [
      {
        kind: "acronym",
        summary: `${token} = ${expansion}`,
        sourceRefs: context.triggerSourceRefs,
      },
    ],
  };
}

/** Production module uses bundled dictionary only until host injects store-backed glossary. */
export const resolveAcronymV1 = createResolveAcronymModule();
