using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ServiceBot.Plugins
{
    public class AIAnalysisPlugin
    {
        private readonly Kernel _kernel;

        public AIAnalysisPlugin(Kernel kernel)
        {
            _kernel = kernel;
        }

        [KernelFunction]
        [Description("Analyzes text content for complaint details.")]
        public async Task<string> AnalyzeTextAsync(
            [Description("The text content to analyze.")] string text,
            [Description("Recent conversation context (optional)")] string? conversationContext = null)
        {
            var contextNote = string.IsNullOrWhiteSpace(conversationContext) 
                ? "" 
                : $"\n\nContext: {conversationContext}";

            var prompt = $@"Analyze text for civic sanitation and environmental health complaints. Be PERMISSIVE and generous in interpretation.

**Accept and extract details for ANY of these issues:**
- Garbage, trash, litter, waste in public areas
- Overflowing bins, illegal dumping, debris
- Blocked/clogged drains, sewers, manholes
- Sewage problems, wastewater, flooding
- Stagnant water, puddles, waterlogging
- Pest infestations (rats, flies, mosquitoes, insects)
- Bad smells, odors from waste
- Broken sanitation infrastructure
- Unsanitary public conditions
- ANY civic/municipal maintenance issues related to cleanliness

**Be flexible:**
- If user describes ANY sanitation problem → ACCEPT
- If conversation context suggests complaint → ACCEPT
- When uncertain → assume it's related

Extract clearly: location (street, area, landmarks), specific issue, severity/urgency.
If truly NOT a sanitation issue (casual chat, greetings, completely off-topic) → respond: 'Not a civic sanitation complaint.'
Be CONCISE in summary.{contextNote}

Text: {{{{$input}}}}
Summary:";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments { ["input"] = text });
            return result.ToString();
        }

        [KernelFunction]
        [Description("Analyzes an image and an optional text caption for complaint details.")]
        public async Task<string> AnalyzeImageAsync(
            [Description("The URL of the image to analyze.")] string imageUrl,
            [Description("Optional text caption provided with the image.")] string caption,
            [Description("Recent conversation context (optional)")] string? conversationContext = null)
        {
            var contextNote = string.IsNullOrWhiteSpace(conversationContext) 
                ? "" 
                : $"\n\nContext: {conversationContext}";

            // Add caption context if provided
            var captionNote = string.IsNullOrWhiteSpace(caption)
                ? "No caption provided - analyze the image objectively for any sanitation issues."
                : $"User provided caption: '{caption}' - use this as context but verify against what you see in the image.";

            var prompt = $@"Analyze this image for civic sanitation and environmental health issues. Be PERMISSIVE and look for ANY sanitation problems.

**YOUR PRIMARY GOAL: Find sanitation issues if they exist in the image.**

**ACCEPT and describe if you see ANY of these (even partially visible):**
- Garbage, trash, litter, waste in ANY location (streets, sidewalks, parks, alleys, roadsides)
- Full, overflowing, or damaged waste bins/dumpsters
- Illegal dumping, piled debris, construction waste
- Blocked, clogged, or dirty drains and sewers
- Sewage problems, wastewater, flooding, leaks
- Stagnant water, puddles, waterlogging in public areas
- Pest infestations (rats, flies, insects, mosquitoes)
- Broken sanitation infrastructure (damaged bins, broken manholes, missing covers)
- Unsanitary conditions in public spaces
- Environmental health hazards (odor sources, contamination)
- ANY visible waste management problems

**Context flexibility:**
- Streets/roads with garbage = VALID (ignore vehicles, people, buildings in background)
- Outdoor areas with any waste visible = VALID
- Public spaces with sanitation issues = VALID
- Urban/rural areas with environmental problems = VALID
- Even small amounts of litter or waste = VALID if it's a complaint

**ONLY reject if the image is clearly:**
- Pure selfie/portrait photo with absolutely NO environment or sanitation issues visible
- Close-up food photo with NO sanitation context
- Screenshot of text/app with NO real-world scene
- Meme, cartoon, or graphic with NO real photo
- Completely unrelated content (clear advertisement, product photo, etc.)

**Important Instructions:**
1. If you see garbage, waste, or sanitation issues → ACCEPT and describe them in detail
2. If caption mentions sanitation issue → trust the user and look carefully for it
3. If no caption → analyze objectively and describe any sanitation problems you find
4. If context suggests complaint → be generous in interpretation
5. When uncertain → assume it's related and describe what you see
6. Focus on FINDING issues, not REJECTING images

{captionNote}

If absolutely NO sanitation issues visible and completely off-topic → respond: 'Not a civic sanitation complaint.'

If ANY sanitation issue found (even minor) → extract: location details (street name, landmarks if visible), specific issue description, severity/urgency indicators.{contextNote}

Image URL: {{{{$imageUrl}}}}

Analyze thoroughly and describe any sanitation problems you find:";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments { ["imageUrl"] = imageUrl, ["caption"] = caption });
            return result.ToString();
        }

        [KernelFunction]
        [Description("Analyzes an audio transcript and optional caption to summarize complaint details.")]
        public async Task<string> AnalyzeAudioAsync(
            [Description("The transcript extracted from the audio message.")] string transcript,
            [Description("Optional text caption or message provided with the audio.")] string caption,
            [Description("Recent conversation context (optional)")] string? conversationContext = null)
        {
            var contextNote = string.IsNullOrWhiteSpace(conversationContext) 
                ? "" 
                : $"\n\nContext: {conversationContext}";

            var prompt = $@"Analyze audio for civic sanitation complaints. Be PERMISSIVE.

**Accept ANY mention of:**
- Garbage, waste, litter, trash problems
- Drains, sewage, flooding issues
- Odors, pest infestations
- Public cleanliness concerns
- Any civic/municipal sanitation issues

Extract: location, issue, severity.
If truly unrelated (greetings, casual chat) → 'Not a civic sanitation complaint.'
Be CONCISE.{contextNote}

Transcript: {{{{$transcript}}}}
Caption: {{{{$caption}}}}
Summary:";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(
                _kernel,
                new KernelArguments
                {
                    ["transcript"] = transcript,
                    ["caption"] = caption
                });
            return result.ToString();
        }

        [KernelFunction]
        [Description("Analyzes the transcript of a video to summarize complaint details.")]
        public async Task<string> AnalyzeVideoAsync(
            [Description("The transcript of the video.")] string videoTranscript,
            [Description("Optional text caption provided with the video.")] string caption,
            [Description("Recent conversation context (optional)")] string? conversationContext = null)
        {
            var contextNote = string.IsNullOrWhiteSpace(conversationContext) 
                ? "" 
                : $"\n\nContext: {conversationContext}";

            var prompt = $@"Analyze video for civic sanitation complaints. Be PERMISSIVE.

**Accept if video shows/describes:**
- Garbage, waste accumulation
- Blocked drains, sewage problems
- Public sanitation issues
- Any civic cleanliness concerns

Only reject if clearly unrelated (vlogs, entertainment, personal content with NO sanitation context).

Be PERMISSIVE. When uncertain → assume it's related.{contextNote}

Video Transcript: {{{{$videoTranscript}}}}
Caption: {{{{$caption}}}}
Summary:";

            var function = _kernel.CreateFunctionFromPrompt(prompt);
            var result = await function.InvokeAsync(_kernel, new KernelArguments { ["videoTranscript"] = videoTranscript, ["caption"] = caption });
            return result.ToString();
        }
    }
}
