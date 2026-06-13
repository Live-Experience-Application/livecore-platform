/**
 * The LiveCore SDK client (CORE-SDK-002): the typed entry point a vertical app
 * uses to call the Core API.
 *
 * It groups the implemented `/api/v1` routes into resource clients that mirror
 * the Core server modules (csv/api_routes.csv) over one shared, authenticated
 * transport. Every method returns the exact `@livecore/contracts` response type
 * for its route, so a vertical consumes a stable, typed Core surface without
 * hand-writing requests. The client is product-neutral: it carries only generic
 * Core vocabulary, and a vertical maps these shapes to its own UI labels
 * (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md).
 *
 * Authorization is enforced server-side; the client only carries the OIDC bearer
 * token and surfaces a denial as a `LiveCoreApiError`
 * (docs/07_SECURITY_THREAT_MODEL.md).
 */
import { HttpClient } from "./http.js";
import type { LiveCoreClientOptions } from "./options.js";
import { AssetsClient } from "./resources/assets.js";
import { ContentClient } from "./resources/content.js";
import { EntitlementsClient } from "./resources/entitlements.js";
import { RealtimeClient } from "./resources/realtime.js";
import { ScenesClient } from "./resources/scenes.js";
import { SessionsClient } from "./resources/sessions.js";
import { StoreClient } from "./resources/store.js";
import { VisibilityClient } from "./resources/visibility.js";
import { WorkspacesClient } from "./resources/workspaces.js";

export class LiveCoreClient {
  /** Workspace create/read/update and member-invite routes. */
  readonly workspaces: WorkspacesClient;
  /** Session start/end lifecycle commands. */
  readonly sessions: SessionsClient;
  /** Scene list and create routes. */
  readonly scenes: ScenesClient;
  /** Content block create route. */
  readonly content: ContentClient;
  /** Reveal command and the participant-visible feed. */
  readonly visibility: VisibilityClient;
  /** Reconnect replay of the durable session event stream. */
  readonly realtime: RealtimeClient;
  /** Asset upload-intent, signed-download and linking flows. */
  readonly assets: AssetsClient;
  /** Quota status and ad-eligibility reads. */
  readonly entitlements: EntitlementsClient;
  /** Purchase verification submissions. */
  readonly store: StoreClient;

  constructor(options: LiveCoreClientOptions) {
    const http = new HttpClient(options);
    this.workspaces = new WorkspacesClient(http);
    this.sessions = new SessionsClient(http);
    this.scenes = new ScenesClient(http);
    this.content = new ContentClient(http);
    this.visibility = new VisibilityClient(http);
    this.realtime = new RealtimeClient(http);
    this.assets = new AssetsClient(http);
    this.entitlements = new EntitlementsClient(http);
    this.store = new StoreClient(http);
  }
}
