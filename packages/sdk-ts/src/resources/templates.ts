/**
 * Templates resource group (CORE-SDK-006): the organization-scoped template
 * create/list/by-id-read and delete routes under
 * `/api/v1/organizations/{organizationSlug}/templates` (csv/api_routes.csv). A
 * template is reusable vertical scaffolding stored as DATA (the template boundary);
 * Core never inspects or branches on its key or definition. All routes are
 * authorized to Owner/Admin only; a non-privileged role is `403` and a foreign
 * tenant/unknown template is hidden as `404` (a `LiveCoreApiError`). The
 * org-scoped routes never return or mutate a global template.
 */
import type {
  CreateTemplateRequest,
  TemplateResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class TemplatesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/organizations/{organizationSlug}/templates` — the organization's
   * templates, in canonical key+version order. Org-scoped only (global templates
   * are never listed).
   */
  list(organizationSlug: string): Promise<TemplateResponse[]> {
    return this.http.send<TemplateResponse[]>({
      method: "GET",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/templates`,
    });
  }

  /**
   * `GET /api/v1/organizations/{organizationSlug}/templates/{templateId}` — read
   * one org-scoped template. A global template addressed by id is hidden as `404`.
   */
  get(organizationSlug: string, templateId: Uuid): Promise<TemplateResponse> {
    return this.http.send<TemplateResponse>({
      method: "GET",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/templates/${encodeURIComponent(templateId)}`,
    });
  }

  /**
   * `POST /api/v1/organizations/{organizationSlug}/templates` — create an
   * org-scoped template (always organization-scoped, never global). The id is
   * assigned server-side; a duplicate per-scope key+version is `409`.
   */
  create(
    organizationSlug: string,
    request: CreateTemplateRequest,
  ): Promise<TemplateResponse> {
    return this.http.send<TemplateResponse>({
      method: "POST",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/templates`,
      body: request,
    });
  }

  /**
   * `DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}` —
   * delete an org-scoped template. A global template is not deletable by an org;
   * already-instantiated entity types are unaffected. Responds `204 No Content`.
   */
  delete(organizationSlug: string, templateId: Uuid): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/templates/${encodeURIComponent(templateId)}`,
    });
  }
}
