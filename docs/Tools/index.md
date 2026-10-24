# Tools

Tools are callable operations the AI can invoke (function/tool calling) and utilities exposed to Grasshopper.

---

## Metadata

| Property | Value |
| --- | --- |
| **Source Code** | `src/SmartHopper.Core.Grasshopper/AITools/` |
| **Since Version** | ? |
| **Last Updated** | 2026-10-24 |
| **Documentation Maintainer** | Codex |

_Note: This documentation was written by AI on its own. It may contain some mistakes. If you would like to help, read this documentation and delete this comment if everything is okay._

---

## Why Read This?

SmartHopper's tool system extends Grasshopper components with AI-powered capabilities such as text generation, image analysis, canvas manipulation, and document conversion. Understanding the tool ecosystem helps you compose powerful parametric + AI workflows.

**You should read this if you:**

- Want to know which AI operations are available in SmartHopper
- Are building a custom component that calls AI tools directly
- Need to understand the tool result envelope convention
- Want to add a new tool to the SmartHopper ecosystem

---

## End-User Guide

See [Canvas and Conversation Tools](canvas-and-conversation-tools.md) for reviewed edits, compact queries, document tools, screenshots, plans, run budgets, and MCP knowledge resources.

### What Are Tools?

Tools are discrete operations with JSON-schema-defined inputs that the AI can invoke during a conversation. They are also exposed as standalone utilities in Grasshopper components. For example, `text2text` generates text, `gh_tidy_up` organizes the canvas, and `img2text` analyzes images.

### When to Use Them

- **Text tasks**: Use `text2text`, `text2boolean`, `text2textlist`, `text2json`
 - **Image & Visual tasks**: Use `img2text` (vision), `text2img` (generation), `canvas_screenshot`, and `viewport_screenshot`
 - **Document & Knowledge tasks**: Use `file2md` or `web2md` to convert documents and web pages to Markdown, and `gh_document_*` to manage open definitions
 - **Canvas tasks**: Use `gh_get` (queries with projection/summary), `gh_put`, `gh_move`, `gh_group`, `gh_tidy_up`, `gh_connect`, `gh_disconnect`, `gh_set_value`, `gh_run` with review approval gates
 - **Planning tasks**: Use `plan_propose` and `plan_tasks` to structure multi-step workflows
- **Knowledge tasks**: Use `smarthopper_readme`, `smarthopper_tool_help`, `mcneel_forum_search` for contextual guidance

### Visual Guide

<!-- PLACEHOLDER: Screenshot showing the Tools panel or component category in Grasshopper -->
<!-- - Location: SmartHopper tab → Tools panel -->
<!-- - Typical wiring: AI component → tool call → result parsing -->

### Common Questions

**Q: How do I know which tools a model supports?**
A: Check the model's capabilities in the AI Models component. Tool calling requires `AICapability.ToolCalling`.

**Q: Can I use tools without the chat interface?**
A: Yes. Many tools are wrapped as standalone Grasshopper components (e.g., `AIImg2TextComponent`, `AIFile2MdComponent`).

---

## Developer Reference

### API Overview

```csharp
// Base contract for all SmartHopper tools
public abstract class AITool
{
    public abstract string Name { get; }
    public abstract string Category { get; }
    public abstract string Description { get; }
    public abstract AICapability RequiredCapability { get; }

    public abstract Task<ToolResult> ExecuteAsync(Dictionary<string, object> parameters);
}

```

### Key Types

| Type | Purpose |
| --- | --- |
| `AITool` | Base class for all tools |
| `AIToolRequest` | Request wrapper with name and parameters |
| `ToolResult` | Structured result with payload and envelope |
| `ToolResultEnvelope` | Standard metadata attached to every result |
| `ToolManager` | Registry and execution dispatcher |

### Code Examples

#### Creating a Simple Tool

```csharp
public class MyCustomTool : AITool
{
    public override string Name => "my_custom_tool";
    public override string Category => "Custom";
    public override string Description => "Does something useful";
    public override AICapability RequiredCapability => AICapability.TextGeneration;

    public override async Task<ToolResult> ExecuteAsync(Dictionary<string, object> parameters)
    {
        var input = parameters["input"].ToString();
        var result = await DoSomethingAsync(input);

        return new ToolResult
        {
            Payload = new Dictionary<string, object>
            {
                { "output", result }
            }
        };
    }
}

```

**Output**: A `ToolResult` with the processed output attached to the `output` key.

#### Executing a Tool Programmatically

```csharp
var toolManager = ToolManager.Instance;

var request = new AIToolRequest("text2text")
{
    Parameters = new Dictionary<string, object>
    {
        { "prompt", "Generate a creative name for a pavilion" },
        { "instructions", "Use architectural terminology" }
    }
};

var result = await toolManager.ExecuteToolAsync(request);
var text = result.Payload["text"].ToString();

```

### Error Handling

| Error | Cause | Solution |
| --- | --- | --- |
| Tool not found | Name mismatch or tool not registered | Verify tool name and that the assembly is loaded |
| Missing parameter | Required parameter not provided | Check tool schema and supply all required fields |
| Capability mismatch | Model does not support required capability | Select a model with the appropriate capability flags |
| Execution timeout | Tool operation took too long | Increase timeout or optimize the tool implementation |

---

## Architecture & Design

### Design Rationale

**Problem**: Grasshopper users need AI-powered operations (text generation, image analysis, canvas manipulation) but there is no standard way to expose them across providers and components.

**Approach**: Define a unified `AITool` contract with JSON-schema inputs and structured outputs. Tools are registered in a central `ToolManager`, formatted by providers into their native API shapes, and executed by components or the chat system.

**Trade-offs**:

- Unified tool interface (portable across providers) vs provider-specific feature limitations
- JSON-schema validation (robust) vs rigid parameter structures
- Central registry (discoverable) vs tight coupling to the tool manager

### Data Flow

```text
Component → AIToolRequest → ToolManager → Provider formatting → AI Model
                                              ↓
                                    ToolResult + ToolResultEnvelope

```

### Tool Categories

| Category | Tools |
| --- | --- |
| Instruction & Knowledge | `smarthopper_readme`, `smarthopper_workflows`, `smarthopper_tool_help`, `file2md`, `web2md`, `mcneel_forum_search`, `mcneel_forum_topic`, `mcneel_forum_post` |
| Text Generation | `text2text`, `text2boolean`, `text2textlist`, `text2json`, `list_filter`, `textlist2boolean` |
| Image Processing | `img2text`, `text2img`, `canvas_screenshot`, `viewport_screenshot` |
| Grasshopper Canvas | `gh_get`, `gh_put`, `gh_move`, `gh_merge`, `gh_group`, `gh_tidy_up`, `gh_list_categories`, `gh_list_components`, `gh_component_preview`, `gh_component_lock`, `gh_connect`, `gh_disconnect`, `gh_set_value`, `gh_run`, `gh_document_list`, `gh_document_read`, `gh_document_switch`, `gh_document_new`, `gh_document_open`, `gh_document_save` |
| Planning | `plan_propose`, `plan_tasks` |
| Scripting | `script_generate`, `script_edit`, `script_review` |

### Related Documentation

- [Canvas and Conversation Tools](./canvas-and-conversation-tools.md)
- [ToolResultEnvelope](./ToolResultEnvelope.md)
- [img2text Tool](./img2text.md)
- [smarthopper_readme Tool](./smarthopper_readme.md)
- [smarthopper_workflows Tool](./smarthopper_workflows.md)
- [smarthopper_tool_help Tool](./smarthopper_tool_help.md)
