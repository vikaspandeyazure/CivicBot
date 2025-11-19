using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ServiceBot.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceBot.Agents
{
    /// <summary>
    /// Orchestrates the complaint handling agent using LLM-driven decision making
    /// </summary>
    public class ComplaintAgentOrchestrator
    {
        private readonly Kernel _kernel;
        private readonly ILogger<ComplaintAgentOrchestrator> _logger;
        private readonly ChatConversationService _chatConvo;

        public ComplaintAgentOrchestrator(
            Kernel kernel,
            ILogger<ComplaintAgentOrchestrator> logger,
            ChatConversationService chatConvo)
        {
            _kernel = kernel;
            _logger = logger;
            _chatConvo = chatConvo;
        }

        /// <summary>
        /// Process user message using agent decision-making
        /// </summary>
        public async Task<AgentResponse> ProcessMessageAsync(
            long chatId,
            string userMessage,
            ComplaintTicket currentTicket,
            bool awaitingConfirmation,
            CancellationToken ct)
        {
            var conversationContext = _chatConvo.GetRecentContext(chatId, maxTurns: 2);
            
            // Step 1: Determine next action using SessionManagementPlugin
            var nextAction = await _kernel.InvokeAsync<string>(
                "SessionManagementPlugin",
                "DetermineNextAction",
                new KernelArguments
                {
                    ["complaintSummary"] = currentTicket.TextSummary ?? string.Empty,
                    ["userMessage"] = userMessage,
                    ["conversationContext"] = conversationContext,
                    ["awaitingConfirmation"] = awaitingConfirmation
                },
                ct);

            _logger.LogInformation("Agent determined next action: {Action}", nextAction);

            // Step 2: Execute action
            return nextAction switch
            {
                "CREATE_TICKET" => await HandleCreateTicketAsync(chatId, currentTicket, ct),
                "CANCEL_TICKET" => await HandleCancelTicketAsync(chatId, ct),
                "END_SESSION" => await HandleEndSessionAsync(chatId, ct),
                "REQUEST_CONFIRMATION" => await HandleRequestConfirmationAsync(chatId, currentTicket, ct),
                "REDIRECT_TO_TOPIC" => await HandleRedirectAsync(chatId, ct),
                "COLLECT_MORE_INFO" => await HandleCollectMoreInfoAsync(chatId, userMessage, currentTicket, ct),
                "CONTINUE_CONVERSATION" => await HandleContinueConversationAsync(chatId, userMessage, currentTicket, ct),
                _ => await HandleContinueConversationAsync(chatId, userMessage, currentTicket, ct)
            };
        }

        private async Task<AgentResponse> HandleCreateTicketAsync(long chatId, ComplaintTicket ticket, CancellationToken ct)
        {
            try
            {
                // Capture conversation history before creating ticket
                ticket.Conversations = _chatConvo.ExportConversationHistory(chatId);
                
                var ticketId = await _kernel.InvokeAsync<string>(
                    "CosmosDbPlugin",
                    "CreateTicket",
                    new KernelArguments { ["ticket"] = ticket },
                    ct);

                _logger.LogInformation("Ticket created successfully. ID: {TicketId}", ticketId);

                // Generate concise confirmation message
                var confirmationMessage = $"Ticket logged!\n\n" +
                                        $"ID: {ticketId}\n" +
                                        $"Status: {ticket.Status}\n\n" +
                                        $"We'll review it shortly. Anything else?";

                return new AgentResponse
                {
                    Message = confirmationMessage,
                    Action = AgentAction.TicketCreated,
                    ShouldClearSession = false, // Keep session alive for "thanks" or follow-up questions
                    TicketId = ticketId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ticket");
                return new AgentResponse
                {
                    Message = "Sorry, couldn't create ticket. Try again?",
                    Action = AgentAction.Error,
                    ShouldClearSession = false
                };
            }
        }

        private async Task<AgentResponse> HandleCancelTicketAsync(long chatId, CancellationToken ct)
        {
            var context = "User cancelled. Say 'No problem!' - keep it brief.";
            var response = await GenerateResponseAsync(chatId, context, ct);

            return new AgentResponse
            {
                Message = response,
                Action = AgentAction.Cancelled,
                ShouldClearSession = true
            };
        }

        private async Task<AgentResponse> HandleEndSessionAsync(long chatId, CancellationToken ct)
        {
            var context = "User said thanks/goodbye. Respond warmly in 1 short sentence.";
            var response = await GenerateResponseAsync(chatId, context, ct);

            return new AgentResponse
            {
                Message = response,
                Action = AgentAction.SessionEnded,
                ShouldClearSession = true
            };
        }

        private async Task<AgentResponse> HandleRequestConfirmationAsync(long chatId, ComplaintTicket ticket, CancellationToken ct)
        {
            var context = $"Complaint summary: '{ticket.TextSummary}'. Ask user to confirm ticket creation. Show brief summary and ask 'Ready to log this?' Be clear but don't repeat every detail they already told you.";
            var response = await GenerateResponseAsync(chatId, context, ct);

            return new AgentResponse
            {
                Message = response,
                Action = AgentAction.RequestingConfirmation,
                ShouldClearSession = false,
                AwaitingConfirmation = true
            };
        }

        private async Task<AgentResponse> HandleRedirectAsync(long chatId, CancellationToken ct)
        {
            var context = "Off-topic. Redirect briefly to sanitation issues.";
            var response = await GenerateResponseAsync(chatId, context, ct);

            return new AgentResponse
            {
                Message = response,
                Action = AgentAction.Redirect,
                ShouldClearSession = false
            };
        }

        private async Task<AgentResponse> HandleCollectMoreInfoAsync(long chatId, string userMessage, ComplaintTicket ticket, CancellationToken ct)
        {
            // Check if this is first interaction (no ticket info yet)
            var isFirstInteraction = string.IsNullOrWhiteSpace(ticket.TextSummary);
            
            string context;
            if (isFirstInteraction)
            {
                context = $"User said: '{userMessage}'. This is their first message. Greet warmly, explain you help with sanitation issues (garbage, drains, sewage, etc), and ask what they need help with. Be friendly and welcoming (2-3 sentences).";
            }
            else
            {
                context = $"Current info: '{ticket.TextSummary}'. Ask for ONE missing detail only (location OR urgency OR duration). Be brief.";
            }
            
            var response = await GenerateResponseAsync(chatId, context, ct);

            return new AgentResponse
            {
                Message = response,
                Action = AgentAction.CollectingInfo,
                ShouldClearSession = false
            };
        }

        private async Task<AgentResponse> HandleContinueConversationAsync(long chatId, string userMessage, ComplaintTicket ticket, CancellationToken ct)
        {
            // Check if this is first interaction
            var isFirstInteraction = string.IsNullOrWhiteSpace(ticket.TextSummary);
            
            string context;
            if (isFirstInteraction)
            {
                context = $"User said: '{userMessage}'. Welcome them warmly and explain how you can help with sanitation issues. Be friendly (2-3 sentences).";
            }
            else
            {
                context = $"Respond naturally but briefly. Don't repeat what user said.";
            }
            
            var response = await GenerateResponseAsync(chatId, context, ct);

            return new AgentResponse
            {
                Message = response,
                Action = AgentAction.Conversing,
                ShouldClearSession = false
            };
        }

        private async Task<string> GenerateResponseAsync(long chatId, string situationContext, CancellationToken ct)
        {
            var prompt = $@"{situationContext}

Be natural and appropriate for the context.";

            return await _chatConvo.GenerateAssistantReplyAsync(chatId, prompt, ct);
        }
    }

    public class AgentResponse
    {
        public string Message { get; set; } = string.Empty;
        public AgentAction Action { get; set; }
        public bool ShouldClearSession { get; set; }
        public bool AwaitingConfirmation { get; set; }
        public string? TicketId { get; set; }
    }

    public enum AgentAction
    {
        Conversing,
        CollectingInfo,
        RequestingConfirmation,
        TicketCreated,
        Cancelled,
        SessionEnded,
        Redirect,
        Error
    }
}
