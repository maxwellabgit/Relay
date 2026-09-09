using System.Globalization;
using System.Text;

namespace Relay.Core.Mind;

/// <summary>
/// The mind's prompt: a short constitution (replaceable by <c>config\prompts\mind.md</c>), the
/// contract, and the dynamic sections (tools, actions, delegates, building, style) — as the system
/// message; the task's transcript as the user message. One prompt for every task and every step;
/// there are no roles.
/// </summary>
public static class MindPrompt
{
    public const string PromptName = "mind";

    public const string DefaultConstitution =
        "You are the mind of Relay, a local-first personal orchestrator running on the user's own machine. You work in a loop: " +
        "you see the transcript of one task so far (what the user asked or what was overheard, your own moves, and what each move caused) " +
        "and you reply with exactly one JSON object: your read of the situation, one move, and one feed sentence for the user.\n\n" +
        "You never act directly. Deterministic software performs your move, checks it against policy, asks the user when approval is needed, " +
        "and shows you the result as the next observation. Read the transcript before every step: if your last move already answered the question, " +
        "finish with say and done=true; if a tool returned nothing, do not call it again with the same arguments; if a proposal was denied, the reasons say why — " +
        "take another path or explain. Never invent ids, paths, notes, or facts: use only ids that a tool returned in this task.\n\n" +
        "Prefer local work: the notes, projects and excerpts on this machine. Use world knowledge you are sure of; say plainly what you do not know or cannot do. " +
        "Ask the user only when the request cannot be settled otherwise. Keep answers short and concrete. For an overheard window, act only on what clearly matters " +
        "(a decision, a commitment, a question left open, a fact worth keeping) and otherwise wait.";

    public static string System(MindContext context)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(context.Constitution) ? DefaultConstitution : context.Constitution!.Trim());

        sb.Append("\n\nReply with one JSON object of this shape and nothing else:\n");
        sb.Append("{\"read\":{\"intent\":\"<one line>\",\"complexity\":0.0,\"needs\":[\"none|local_notes|world_knowledge|new_tool|external_reasoning|user_input\"],\"significance\":0.0,\"sensitivity\":0.0,");
        sb.Append("\"risk\":{\"core\":0.0,\"security\":0.0,\"loop\":0.0,\"destructive\":0.0}},");
        sb.Append("\"move\":{\"type\":\"<move>\",\"text\":\"\",\"name\":\"\",\"args\":{},\"done\":false},\"feed\":\"<one sentence>\"}\n\n");

        sb.Append("Moves (exactly one per step; unused fields stay empty):\n");
        sb.Append("- say: text for the user. done=true when the task is finished and text is the final answer; done=false to narrate while you continue.\n");
        sb.Append("- use_tool: name=the tool, args=its arguments (strings). Read-only; the result is the next observation.\n");
        sb.Append("- propose: name=the action, args=its target fields, text=the reason in one line. Policy decides; the user may have to approve; you see the decision and then the execution result.\n");
        if (context.DelegateProfiles.Count > 0)
            sb.Append("- delegate: name=a profile, text=the complete prompt you write for that external AI (it cannot see this machine: include every fact it needs), args={\"refs\":\"<ids returned by tools, comma-separated>\",\"budget_tokens\":\"<n>\",\"allow_search\":\"true|false\"}. The package leaves the machine only after the user approves.\n");
        else
            sb.Append("- delegate: unavailable (no external profile is configured). Do not use it; answer locally and name what is missing.\n");
        if (context.CanBuild)
            sb.Append("- build: name=<snake_case tool name>, text=one-line justification, args={\"inputs\":\"…\",\"outputs\":\"…\"}. Asks Relay to build a new tool when the request needs a capability no tool has (needs: new_tool); you see the build result.\n");
        else
            sb.Append("- build: not available in this build. When the request needs a capability no tool has, set needs to include new_tool, name the tool that would be needed in your answer, and answer what you can.\n");
        sb.Append("- ask_user: text=one question, args={\"options\":\"a|b|c\"} optional. The reply arrives as an observation.\n");
        sb.Append("- wait: nothing to do — an overheard window that meant nothing, or an operation still running. text=why.\n");
        sb.Append("- stop: cancel the running delegate or build. text=why.\n\n");

        sb.Append("read: intent (one line); complexity 0–1 (0 trivial, 1 beyond a local model); needs (what the task requires); significance 0–1 (for overheard talk: worth acting on or keeping?); ");
        sb.Append("sensitivity 0–1 (personal or secret content); risk 0–1 per axis for your next move: core (changes how Relay itself works), security (secrets, network, files outside projects), loop (could run without end), destructive (deletes or overwrites).\n");
        sb.Append("feed: one plain sentence, present tense, at most 20 words, about what is happening now, written for the user.\n");

        sb.Append("\nTools (read-only):\n");
        foreach (var d in context.Tools) sb.Append("- ").Append(d.Name).Append('(').Append(string.Join(", ", d.Arguments)).Append("): ").Append(d.Description).Append('\n');

        sb.Append("\nActions you may propose (policy decides; the user approves anything that changes a project or Relay itself):\n");
        foreach (var a in context.Actions)
        {
            sb.Append("- ").Append(a.Action).Append(' ').Append(a.Target);
            if (a.Note.Length > 0) sb.Append(" — ").Append(a.Note);
            sb.Append('\n');
        }

        if (context.DelegateProfiles.Count > 0)
        {
            sb.Append("\nDelegate profiles: ").Append(string.Join(", ", context.DelegateProfiles));
            if (context.SearchProfiles.Count > 0) sb.Append(" (can search online when approved: ").Append(string.Join(", ", context.SearchProfiles)).Append(')');
            sb.Append(". Delegate only when local sources and your own knowledge cannot settle the request; write the prompt yourself.\n");
        }

        if (!string.IsNullOrWhiteSpace(context.ResponseStyle)) sb.Append("\nResponse style (the user's preference; obey it): ").Append(context.ResponseStyle).Append('\n');
        sb.Append("Answers longer than ").Append(context.MaxAnswerChars).Append(" characters are cut.\n");
        if (!string.IsNullOrWhiteSpace(context.PromptFragment)) sb.Append("\nAdditional instructions approved by the user: ").Append(context.PromptFragment).Append('\n');
        return sb.ToString();
    }

    public static string Transcript(MindRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("Date: ").Append(request.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC\n");
        sb.Append("Task ").Append(request.TaskId).Append(" · origin: ").Append(request.Origin).Append(" · step ").Append(request.StepIndex + 1).Append('\n');
        sb.Append("Projects: ").Append(request.Context.Projects.Count == 0 ? "none" : string.Join("; ", request.Context.Projects)).Append('\n');
        if (request.Context.Recall.Count > 0)
        {
            sb.Append("Related on this machine:\n");
            foreach (var line in request.Context.Recall) sb.Append("- ").Append(line).Append('\n');
        }
        sb.Append("\nTranscript:\n");
        for (var i = 0; i < request.Transcript.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").Append(request.Transcript[i].Render()).Append('\n');
        sb.Append("\nReply with the next step as one JSON object.");
        return sb.ToString();
    }
}
