# Ringfour Apps — Website Specification

**Version:** 1.0 · **Date:** November 2026 · **Scope:** `/apps` and `/apps/:slug`

## 1. Objective

Make **Apps** a first-class Ringfour product surface between **SDK** and **Studio**. The site should communicate an emerging ecosystem of complete, configurable robot applications: developers build applications with the open SDK; people discover and adapt them through Apps; operators deploy and manage them using Ringfour tooling and Studio as it becomes available.

**Core promise:** Build an application once. Configure it for different tasks. Adapt it to different robots. Share it with the world.

This is a product direction, not a claim of universal hardware portability or an already operational app marketplace. Clearly distinguish available functionality from concepts and roadmap.

### Non-goals

- No blocks, visual programming, drag-and-drop mission editor, or component marketplace.
- No fake app installs, ratings, reviews, publisher statistics, or unsupported compatibility claims.
- No requirement that Ringfour handle transactions between developers, integrators, OEMs, and customers.
- No implication that Ringfour certifies third-party mission behavior or robot safety.
- No rebuilding the SDK or Studio pages from scratch.

## 2. Navigation and routing

Top-level order: **SDK → Apps → Studio**.

| Route | Purpose |
|---|---|
| `/` | Introduce Ringfour as a robot application ecosystem; link to Apps prominently |
| `/sdk` | Build, package, and run robot applications |
| `/apps` | Discover featured applications and understand the ecosystem |
| `/apps/object-inventory` | Object Inventory detail |
| `/apps/camera-bot` | Camera Bot detail |
| `/apps/inspection` | Inspection detail |
| `/studio` | Operate/deploy applications; mark unreleased capabilities Coming Soon |

Keep the site header and footer consistent. Breadcrumbs on app detail pages: **Apps / [App Name]**. Deep links must work on refresh. Unknown slugs render a proper not-found state.

## 3. Visual and editorial direction

- Keep existing Ringfour visual language: **Space Grotesk**, industrial minimalism, precise typography, restrained playfulness, generous spacing.
- Colors: Ringfour orange `#E47A00`; SDK violet `#9568F5`; Studio blue `#527F9B`; graphite `#0B1114`; bone `#F5F5F5`; paper `#EEEEEE`; dark canvas `#080808`; dark surface `#111111`; teal `#36B5A0` for actual success states.
- Apps should feel like a **product catalog**, not a SaaS pricing page or a generic robotics blog.
- Prefer real robot photos, actual app screenshots, annotated configuration screenshots, and short demo clips. If unavailable, use clearly labeled concept artwork or tasteful placeholders; do not imply concept visuals are real deployments.
- Copy should be confident, technically literate, and accessible to developers, integrators, OEMs, and operators. Avoid grandiose AGI claims, vague “AI-powered everything,” and fake marketplace maturity.
- Use consistent release-state labels: **Concept**, **In Development**, **Preview**, **Available**. Only **Available** when a runnable, documented release actually exists.

## 4. Apps landing page — `/apps`

### 4.1 Hero

**Eyebrow:** `RINGFOUR / APPS`

**Headline:** `Good robots deserve good apps.`

**Supporting copy:** `Discover applications for real robots. Configure them for your task, adapt them to your hardware, and make them your own.`

Optional supporting line: `Built with the Ringfour SDK. Designed to go further than one robot.`

Primary CTA: **Explore apps** (scroll to featured catalog). Secondary CTA: **Build an app** (link to SDK/docs). No fake store-search UI or misleading “Install now” CTA.

### 4.2 Featured applications

Show three strong, editorially curated cards, not a large empty marketplace grid. Each card has:

- App name, one-line value proposition, category, release status.
- Real or clearly identified conceptual visual.
- Three to five illustrative configuration capabilities.
- Detail-page link, optionally source/demo link if it exists.

Initial apps:

1. **Object Inventory** — `Find, identify, and catalog objects in your environment.` Category: Perception / Inventory. Configurable objects, detection model, search area, confidence threshold, reporting.
2. **Camera Bot** — `Your own autonomous camera operator.` Category: Tracking / Video. Configurable subject, framing, following distance, camera/recording behavior, optional voice and LLM commands.
3. **Inspection** — `Observe what matters. Report what changes.` Category: Inspection / Automation. Configurable inspection targets, route/waypoints, evaluation criteria, schedule, alerts, reporting.

Each card navigates to its own page. Display actual implementation status per app; default to **Concept** until verified otherwise.

### 4.3 How it works

Three steps: **Discover → Configure → Deploy**.

- **Discover:** Find a reusable robot workflow and understand what it needs.
- **Configure:** Set its targets, models, parameters, and supported hardware integration.
- **Deploy:** Run and operate it using Ringfour tooling; explain when a Studio workflow is planned rather than available.

Emphasize that configuration changes parameters and behavior within an app's supported contract. Hardware adaptation or new capabilities may require developer/integrator work.

### 4.4 Ecosystem section

**Heading:** `Your application. Their robot. Everyone wins.`

Explain the independent roles:

- Developers publish useful applications and build reputation or commercial opportunities.
- Integrators adapt applications for specific robots, environments, and customers.
- Manufacturers enable third-party software on their hardware.
- Operators gain capabilities without commissioning every workflow from scratch.
- Ringfour provides the shared application tooling, discovery surface, and operational infrastructure.

Do not imply mandatory Ringfour commissions or that the company assumes responsibility for third-party integrations.

### 4.5 Closing CTA

**Heading:** `Have an application worth sharing?`

**Copy:** `Build with the Ringfour SDK. Help shape the first generation of robot apps.`

CTA: **Explore the SDK**. Optional **Talk to us** only if a real contact route exists. No nonfunctional submission form.

### 4.6 Catalog expansion behavior

For the first three apps, avoid unnecessary search/filter controls. When catalog grows, support category, robot capability, supported hardware, publisher, and release status filters. These are later enhancements, not v1 prerequisites.

## 5. Application detail page — `/apps/:slug`

Use one reusable detail template driven by typed app metadata. The page must answer: **What does it do? What can I configure? What hardware does it need? How do I get it? Who maintains it?**

### 5.1 Header / hero

- Breadcrumb: `Apps / [App name]`.
- App name, category, concise benefit statement, release-status badge.
- Publisher/maintainer name and source link if known.
- Primary demo media: real video, screenshots, or explicitly labeled concept visual.
- Primary CTA depends on state:
  - **Available:** `View source` / `Get started` only if links are real.
  - **Preview:** `View preview` or `View source` when applicable.
  - **In Development / Concept:** `Explore the concept` or a genuine documentation/contact link; never `Install`.
- Secondary CTA: `Build with SDK`.

### 5.2 Overview

Two or three paragraphs describing the end-to-end workflow and expected outputs. Include a short `What it does` list with concrete steps. Separate demonstrated functionality from planned extensions.

### 5.3 Configuration (central section)

**Heading:** `Make it yours.`

Show representative settings in a realistic, clearly labeled configuration preview. Prefer read-only examples until actual configuration integration exists. If interactive, label it **Example configuration — not connected to a robot** and do not suggest that editing deploys anything.

For each setting show label, type, default/example, explanation, and any prerequisites. Organize settings into groups such as **Task**, **Perception**, **Motion**, **Output**, and **Optional AI**. Distinguish required from optional.

The app defines a **configuration contract**; Studio/CLI may eventually render/validate it. Configuration is not a visual block programming system. No universal semantics are imposed on missions; app authors define behavior and handlers.

### 5.4 Compatibility and requirements

A dedicated section with:

- Required robot capabilities (e.g., camera feed, motion control, localization).
- Optional capabilities (e.g., microphone, depth camera, GPU/NPU, arm/gripper).
- Verified robot/platform integrations, only when actually tested.
- Known limitations and adaptation notes.
- Compute and runtime requirements when known.
- Explicit compatibility vocabulary: **Verified**, **Community-reported**, **Requires adaptation**, **Not tested**. Do not use `Compatible` without evidence.

Use a concise notice: `Hardware support depends on the robot's drivers, capabilities, and integration. Some deployments require an integrator.`

### 5.5 How it works

A simple four-stage explanation: **Inputs → Application logic → Robot actions / HITL → Outputs**. Keep diagrams app-specific. Explain where ROS2/drivers provide hardware interfaces and where Ringfour manages mission execution. Avoid making every app appear to need an LLM.

### 5.6 Example scenarios

Show 2–4 distinct uses of the same application, emphasizing that different configurations yield different outcomes without rebuilding core logic.

### 5.7 Developer and integrator section

Explain extension points: configuration schema, model/provider replacement, hardware adapters, mission code, output/report integrations. Link to SDK docs, source, and integration guidance when real. State that integrators can work directly with customers; Ringfour need not intermediate contracts.

### 5.8 Maintainer, version, license, and provenance

When available, show publisher, repository, version, license, last updated, release notes, and demo provenance. Never fabricate these values. For unreleased concepts, show `Concept — implementation and availability not yet announced` instead of empty technical metadata.

### 5.9 Related apps / closing CTA

Link to the other two featured apps and back to `/apps`. Final CTA: `Build your own robot app` → SDK.

## 6. Initial app detail content

### 6.1 Object Inventory — `/apps/object-inventory`

**Tagline:** `Know what's where.`

**Description:** A mobile robot surveys a defined area, identifies selected objects, and produces a structured inventory. The application defines the survey/detection/reporting workflow; users configure the objects and reporting requirements.

**Example configuration:**

| Field | Type | Example | Notes |
|---|---|---|---|
| Target objects | list of strings/classes | `toolbox, extinguisher, pallet` | Required |
| Detection model | model reference | `configured detector` | Model availability depends on deployment |
| Survey area | area/waypoint reference | `lab-west` | Requires appropriate navigation capability |
| Confidence threshold | float 0–1 | `0.75` | Example only |
| Record images | boolean | `true` | Storage/privacy considerations |
| Output format | enum | `CSV / JSON` | Illustrative |

**Required capabilities:** Camera, access to a defined observation path or area, object detection inference, data storage. Navigation may be supplied by robot or external integration. **Outputs:** Object list, observations, timestamps, optional location and images. **Scenarios:** Lab equipment audit, warehouse shelf survey, tool tracking. **Limitations:** Detection accuracy and spatial localization depend on sensors, model, environment, and robot integration.

### 6.2 Camera Bot — `/apps/camera-bot`

**Tagline:** `Your robot, your camera crew.`

**Description:** A mobile camera operator follows a designated subject and adjusts movement and framing according to configured rules. Optional voice control or an LLM can interpret operator requests, but deterministic tracking and safety limits remain outside the LLM's authority.

**Example configuration:**

| Field | Type | Example | Notes |
|---|---|---|---|
| Tracking target | target selector | `person / selected object` | Required |
| Following distance | distance | `1.5 m` | Subject to robot limits |
| Framing mode | enum | `center / rule-of-thirds` | Depends on camera actuation |
| Maximum speed | speed | `0.4 m/s` | Bounded by robot safety settings |
| Recording mode | enum | `manual / while tracking` | Requires recording integration |
| Voice commands | boolean | `false` | Optional microphone + speech pipeline |
| LLM direction | boolean | `false` | Optional, with explicit command validation |

**Required capabilities:** Camera, target tracking, mobile motion interface, appropriate collision avoidance/safety handling. **Optional:** Pan/tilt, microphone, local or remote LLM. **Outputs:** Video, tracking events, optional command log. **Scenarios:** Solo creator filming, educational demonstrations, telepresence content. **Limitations:** Occlusion, lighting, motion blur, safety near people, and audio/video consent. Do not promise autonomous safe following without tested safeguards.

### 6.3 Inspection — `/apps/inspection`

**Tagline:** `Observe what matters.`

**Description:** A robot performs repeatable observations at designated locations, evaluates user-defined conditions, and produces reports or requests human review. What is inspected is a configuration and integration decision, not a fixed Ringfour-defined domain.

**Example configuration:**

| Field | Type | Example | Notes |
|---|---|---|---|
| Inspection targets | list | `gauge-1, valve-3` | Required |
| Observation plan | waypoint/route reference | `plant-round-a` | Hardware-dependent |
| Evaluation method | enum/provider | `threshold / vision model` | App-defined |
| Alert condition | expression/rule reference | `out-of-range` | Validate explicitly |
| Schedule | schedule | `daily 09:00` | Only if scheduling supported |
| Human review | boolean | `true` | HITL support/status must be truthful |
| Report destination | output connector | `local JSON` | Other connectors optional |

**Required capabilities:** Appropriate observation sensors, repeatable access to targets, output/logging. **Optional:** Navigation, thermal/depth sensors, LLM-assisted interpretation, HITL approvals. **Outputs:** Inspection observations, anomalies, audit trail, reports. **Scenarios:** Machinery visual checks, facility rounds, laboratory monitoring. **Limitations:** A configurable observation workflow is not a certified industrial inspection system; reliability depends on models, sensors, environment, and human review policy.

## 7. Suggested data model

Keep catalog data separate from page components; start with static typed data and migrate to a backend when publishing actually exists.

```ts
type ReleaseStatus = 'concept' | 'in-development' | 'preview' | 'available';
type CompatibilityStatus = 'verified' | 'community-reported' | 'requires-adaptation' | 'not-tested';

type AppListing = {
  slug: string;
  name: string;
  tagline: string;
  summary: string;
  category: string;
  status: ReleaseStatus;
  publisher?: { name: string; url?: string };
  media: Array<{ kind: 'image' | 'video'; src: string; alt?: string; provenance: 'real' | 'concept' }>;
  features: string[];
  useCases: string[];
  configuration: Array<{
    key: string;
    label: string;
    group: string;
    type: 'string' | 'number' | 'boolean' | 'enum' | 'list' | 'reference';
    required: boolean;
    example?: unknown;
    description: string;
    options?: string[];
  }>;
  capabilities: { required: string[]; optional: string[] };
  compatibility: Array<{ robot: string; status: CompatibilityStatus; notes?: string }>;
  limitations: string[];
  repositoryUrl?: string;
  documentationUrl?: string;
  demoUrl?: string;
  version?: string;
  license?: string;
  updatedAt?: string;
};
```

This is a **website content model**, not a commitment to a final SDK manifest or configuration-schema API. Align with actual SDK contracts before implementing runtime configuration or install flows.

## 8. Homepage updates

- Give Apps equal conceptual prominence to SDK and Studio.
- Lead with the robot application ecosystem: **build → discover/configure → deploy/operate**.
- Feature the three initial applications with direct links to detail pages.
- Explain hardware adaptation as an ecosystem opportunity, not guaranteed universal binary portability.
- Keep Studio's unreleased features clearly marked **Coming Soon**.
- Preserve the existing playful, technically confident Ringfour tone and brand rather than replacing the homepage with a generic marketplace template.

## 9. Interaction, responsive, accessibility, SEO

- Mobile-first layouts: cards stack on narrow screens; detail-page media and configuration sections remain readable; avoid horizontal overflow.
- All CTAs must lead to working routes or actual resources. No inert install, submit, purchase, or configure controls.
- Use semantic headings, keyboard-focus indicators, accessible contrast, descriptive media alt text, and labeled inputs if an interactive preview is built.
- Respect reduced-motion preferences; videos should not autoplay with sound.
- Add page-specific title, description, canonical URL, and Open Graph preview; detail pages should use app-specific metadata.
- Provide graceful media fallbacks, empty states, and proper 404 handling.
- Treat configuration examples as illustrations until backed by an actual runtime configuration workflow.

## 10. Implementation plan

**Phase A — Website MVP (now)**

1. Add Apps to global navigation between SDK and Studio.
2. Create `/apps` with hero, three featured app cards, workflow explanation, ecosystem section, and CTA.
3. Create reusable `/apps/:slug` template and populate the three detail pages.
4. Update homepage to foreground Apps and link to featured applications.
5. Clearly label statuses, demo provenance, and roadmap functionality.
6. Verify mobile/desktop layout, navigation, accessibility basics, and deep-link refresh.

**Phase B — Real demos (as applications mature)**

1. Replace concept imagery with actual screenshots/videos.
2. Publish source and documentation links for runnable apps.
3. Add verified compatibility records from tested robots.
4. Expose actual app configuration examples based on real SDK contracts.
5. Capture deployment case studies, including affordable-to-commercial-robot scaling.

**Phase C — Hub functionality (only when justified)**

Publisher profiles, submission/review, catalog search and filtering, versioned releases, app metadata ingestion, compatibility contributions, private catalogs, and Studio-integrated installation/configuration. Do not build these just to make the initial website look like a mature store.

## 11. Acceptance criteria

- [ ] Global navigation reads SDK → Apps → Studio and highlights the active page.
- [ ] `/apps` explains the product in one screen and prominently displays exactly the three flagship apps.
- [ ] Each app card opens a unique, functioning detail route.
- [ ] Every detail page includes overview, configuration, requirements/compatibility, scenarios, limitations, and developer information.
- [ ] Configuration is central to the design, but no blocks or visual workflow builder are introduced.
- [ ] No release, demo, compatibility, or operational claim is made without supporting evidence.
- [ ] Unavailable install/publish/deploy actions are not presented as working buttons.
- [ ] Homepage communicates SDK → Apps → Studio as one ecosystem.
- [ ] Existing Ringfour visual language is retained.
- [ ] Responsive layouts, keyboard navigation, metadata, and 404 behavior work.
- [ ] Initial catalog is maintainable through structured data rather than duplicated page code.

## 12. Definition of success

A first-time visitor should understand within a minute that Ringfour is a way to **build, discover, configure, adapt, and operate useful robot applications**. A developer should see a route to publishing reusable work; an integrator should see a route to adapting it for customers; a manufacturer should see an ecosystem opportunity; and an operator should understand what can be configured without writing code. The site must communicate this vision credibly, even while the first apps and store capabilities are still being developed.
