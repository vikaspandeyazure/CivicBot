using Microsoft.SemanticKernel;
using ServiceBot.Models;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceBot.Plugins.Session
{
    /// <summary>
    /// Plugin for managing user session and complaint ticket state
    /// </summary>
    public class SessionManagementPlugin
    {
        private readonly Kernel _kernel;

        public SessionManagementPlugin(Kernel kernel)
        {
            _kernel = kernel;
        }

        [KernelFunction]
        [Description("Analyze if the user's message indicates they want to confirm/create a ticket")]
        public async Task<bool> IsUserConfirmingTicketAsync(
            [Description("The user's message text")] string userMessage,
            [Description("Recent conversation context")] string conversationContext,
            CancellationToken ct = default)
        {
            var prompt = $@"Analyze if the user wants to confirm and create a civic complaint ticket.

User message: ""{userMessage}""

Recent conversation:
{conversationContext}

Confirmation indicators:
- Yes, okay, sure, confirm, proceed, go ahead
- ""log the ticket"", ""create ticket"", ""submit it""
- ""all information is good""
- Any affirmative response

Respond with ONLY 'true' or 'false'.";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments(), ct);
            
            var response = result.ToString().Trim().ToLowerInvariant();
            return response.Contains("true");
        }

        [KernelFunction]
        [Description("Analyze if the user wants to cancel/discard the current complaint")]
        public async Task<bool> IsUserCancellingAsync(
            [Description("The user's message text")] string userMessage,
            [Description("Recent conversation context")] string conversationContext,
            CancellationToken ct = default)
        {
            var prompt = $@"Analyze if the user wants to cancel or discard the complaint ticket.

User message: ""{userMessage}""

Recent conversation:
{conversationContext}

Cancellation indicators:
- No, cancel, stop, discard, ignore, nevermind
- ""don't create"", ""don't log""
- Any negative response indicating they don't want to proceed

Respond with ONLY 'true' or 'false'.";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments(), ct);
            
            var response = result.ToString().Trim().ToLowerInvariant();
            return response.Contains("true");
        }

        [KernelFunction]
        [Description("Analyze if the user's message indicates the conversation is ending (thanks, goodbye, etc)")]
        public async Task<bool> IsConversationEndingAsync(
            [Description("The user's message text")] string userMessage,
            [Description("Recent conversation context")] string conversationContext,
            CancellationToken ct = default)
        {
            var prompt = $@"Analyze if the user is ending the conversation or expressing gratitude after completing their task.

User message: ""{userMessage}""

Recent conversation:
{conversationContext}

Ending indicators:
- Thanks, thank you, appreciate it, bye, goodbye
- ""that's all"", ""nothing else"", ""all done""
- Gratitude expressions after ticket was created
- Any closing statement

Respond with ONLY 'true' or 'false'.";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments(), ct);
            
            var response = result.ToString().Trim().ToLowerInvariant();
            return response.Contains("true");
        }

        [KernelFunction]
        [Description("Determine the next best action based on conversation state")]
        public async Task<string> DetermineNextActionAsync(
            [Description("Current complaint summary")] string complaintSummary,
            [Description("User's latest message")] string userMessage,
            [Description("Recent conversation context")] string conversationContext,
            [Description("Whether user is awaiting confirmation")] bool awaitingConfirmation,
            CancellationToken ct = default)
        {
            // CRITICAL: If awaiting confirmation, check for explicit confirmation first
            if (awaitingConfirmation && !string.IsNullOrWhiteSpace(complaintSummary))
            {
                // Check for explicit confirmation phrases first
                var lowerMsg = userMessage.ToLowerInvariant().Trim();
                
                // Strong confirmation indicators
                if (lowerMsg.Contains("log") || 
                    lowerMsg.Contains("create") || 
                    lowerMsg.Contains("submit") ||
                    lowerMsg.Contains("confirm") ||
                    lowerMsg.Contains("proceed") ||
                    lowerMsg.Contains("go ahead") ||
                    lowerMsg == "yes" ||
                    lowerMsg == "y" ||
                    lowerMsg == "ok" ||
                    lowerMsg == "okay" ||
                    lowerMsg == "sure" ||
                    lowerMsg.Contains("please log") ||
                    lowerMsg.Contains("log it") ||
                    lowerMsg.Contains("log the ticket") ||
                    lowerMsg.Contains("create ticket") ||
                    lowerMsg.Contains("create it"))
                {
                    return "CREATE_TICKET";
                }
                
                // Strong cancellation indicators
                if (lowerMsg.Contains("cancel") ||
                    lowerMsg.Contains("no") ||
                    lowerMsg.Contains("don't") ||
                    lowerMsg.Contains("discard") ||
                    lowerMsg.Contains("stop") ||
                    lowerMsg.Contains("ignore"))
                {
                    return "CANCEL_TICKET";
                }
            }

            var prompt = $@"You are an intelligent agent managing a civic complaint bot conversation. Determine the next action.

Current State:
- Complaint Summary: {(string.IsNullOrWhiteSpace(complaintSummary) ? "None yet" : complaintSummary)}
- User Message: ""{userMessage}""
- Awaiting Confirmation: {awaitingConfirmation}

Recent Conversation:
{conversationContext}

CRITICAL DECISION RULES (in priority order):
1. If Awaiting Confirmation = true AND user says yes/confirm/log/create ? CREATE_TICKET
2. If Awaiting Confirmation = true AND user says no/cancel ? CANCEL_TICKET
3. If Complaint Summary exists AND user says ""log"", ""create ticket"", ""submit"" ? CREATE_TICKET
4. If user says thanks/goodbye after complaint discussed ? END_SESSION
5. If user asks off-topic questions ? REDIRECT_TO_TOPIC
6. If Complaint Summary exists but not awaiting confirmation ? REQUEST_CONFIRMATION
7. If no complaint info yet ? COLLECT_MORE_INFO
8. Otherwise ? CONTINUE_CONVERSATION

Available Actions:
- CREATE_TICKET: User confirmed, create the ticket NOW
- CANCEL_TICKET: User wants to cancel
- END_SESSION: User is done (said thanks/goodbye after ticket created)
- REQUEST_CONFIRMATION: Have enough info, ask user to confirm ticket creation
- COLLECT_MORE_INFO: Need more details about the complaint
- REDIRECT_TO_TOPIC: User went off-topic, redirect to sanitation issues
- CONTINUE_CONVERSATION: Continue natural conversation

IMPORTANT: When user says ""log a ticket"", ""please log it"", ""create ticket"" ? respond with CREATE_TICKET

Respond with ONLY the action name (e.g., 'CREATE_TICKET').";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments(), ct);
            
            return result.ToString().Trim().ToUpperInvariant();
        }
    }
}
