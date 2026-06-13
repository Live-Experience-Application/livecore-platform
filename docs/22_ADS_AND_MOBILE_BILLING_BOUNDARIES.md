# Ads and Mobile Billing Boundaries

## Rule

Core may decide whether a subject is eligible to see ads. Core must not render, request, configure or display ads.

## Core owns

```text
AdEligibilityPolicy
AdEligibilityResult
Entitlement-driven ad-free state
Audit of entitlement changes
```

## Mobile owns

```text
Ad SDK integration
Ad placement
Ad frequency capping
Consent UI
ATT prompt timing
Google consent flows
AdMob unit IDs
Native ad rendering
Paywall UI
Store product display
```

## API

```text
GET /v1/me/ad-eligibility
```

Example response:

```json
{
  "adsRequired": true,
  "reason": "NO_AD_FREE_ENTITLEMENT",
  "sessionAdFreeUntil": null,
  "hostedSessionAdFree": false
}
```

## Ad eligibility policy and endpoint (CORE-ADS-001)

CORE-ADS-001 implements the two Core-owned ad types above — `AdEligibilityPolicy` and `AdEligibilityResult` —
and the single read that exposes them, so that **Core returns ad eligibility without knowing ad placements**
(the epic's acceptance criterion). It lives in the Entitlements module (`apps/api/Entitlements/`), because ad
eligibility is entirely **entitlement-driven**; it adds no table and no EF migration (it reuses the CORE-ENTL-002
`SubjectEntitlementResolver`), and Core still never renders, requests, configures or places ads.

- `GET /api/v1/me/ad-eligibility` (`apps/api/Entitlements/AdEligibilityEndpoints.cs`) — the route of
  `csv/mobile_store_api_routes.csv` (`GET /v1/me/ad-eligibility`) surfaced under the Core `/api/v1` prefix
  `docs/08_API_CONTRACTS.md` mandates, and added to `csv/api_routes.csv`. It returns whether the **current user**
  must be shown ads. The response is exactly the documented shape:

  ```json
  {
    "adsRequired": true,
    "reason": "NO_AD_FREE_ENTITLEMENT",
    "sessionAdFreeUntil": null,
    "hostedSessionAdFree": false
  }
  ```

  It carries only the generic, entitlement-derived decision — never an ad placement, an ad provider/unit id or any
  SDK configuration (the "Forbidden" list below; threat T7). A vertical maps the generic `reason` code to its own
  paywall copy and owns all ad rendering.

- **Entitlement-driven, fail-closed.** `AdEligibilityPolicy.Evaluate` is a pure function of the subject's resolved
  `EffectiveEntitlements` (the central CORE-ENTL-002 resolver output — entitlement logic is not duplicated). Ads are
  required by default; ad-free state is granted only by an explicit, active server entitlement, so a subject with no
  entitlements — or only a client-asserted one the server never recorded — sees ads ("Never trust client-side
  premium flags"; `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`). The decision reads only the subject's own
  entitlements, so one user's premium state is never returned through another's id (per-subject isolation, threat
  T5). The generic flag keys it consults (`docs/21` "Generic entitlement keys";
  `csv/mobile_entitlement_catalog.csv`):

  - `ads.disabled` = `true` (the personal ad-free grant) ⇒ `adsRequired: false`,
    `reason: AD_FREE_ENTITLEMENT`. This grant overrides the ad-bearing marker.
  - otherwise `ads.required` = `true` (the explicit ad-bearing plan marker) ⇒ `adsRequired: true`,
    `reason: ADS_REQUIRED_ENTITLEMENT`.
  - otherwise (no relevant grant) ⇒ `adsRequired: true`, `reason: NO_AD_FREE_ENTITLEMENT` (the fail-closed
    default).

  `hostedSessionAdFree` is reported **independently** from the `hosted.sessions.ads.disabled` flag (the
  host's "table pass" capability that removes ads for participants in sessions they host); it never changes the
  subject's own `adsRequired`. `sessionAdFreeUntil` is part of the contract shape for a future, mobile-driven
  temporary (rewarded-ad) ad-free window; Core has no persisted temporary session ad-free grant yet, so it is
  currently always `null`.

- **Authorization.** A missing/invalid token is `401`. `/me` is an inherently per-user concept (the buyer's own
  premium state), so a non-user **service-account** principal is denied `403` — it has no personal ad eligibility
  (the same rule as the `/me/quota-status` read and the purchase endpoints). The current user is resolved through the
  canonical user-profile reference; the decision is for the **User** subject keyed by the profile id, and the
  response carries no subject id. The user's ad eligibility spans the deployment (it follows the user's purchase, not
  a tenant), so there is no organization/workspace boundary on this route. Like the other persistence-backed reads,
  the endpoint fails closed with `503` when no database is configured.

- **Out of scope (later stories).** A temporary per-session (rewarded-ad) ad-free window that would populate
  `sessionAdFreeUntil`, and the product → plan → entitlement mapping that grants `ads.disabled` from a verified
  purchase, are later stories; CORE-ADS-001 is the policy and the read over the entitlements those stories grant.
  The verified-purchase → `ads.disabled` grant is part of the purchase-to-entitlement chain that **CORE-DOC-002
  formally defers to post-v1** (billing is out of scope for Core v1, `docs/01_PRODUCT_VISION_AND_SCOPE.md`; see
  `docs/24_SPEC_CONSISTENCY.md`). Until then this read reflects only entitlements assigned by other means, and a
  subject with no ad-free grant fails closed to ads-required.

## Forbidden

Do not put this in Core:

```text
BannerAd
InterstitialAd
RewardedAd
AdMob
IDFA
ATT prompt copy
Player Premium copy
GM Pro copy
```

Ad provider names may appear only in deployment/configuration docs or provider infrastructure modules, not in Core domain logic.

## Recommended ad policy for ArcanOS

Ads must never interrupt live reveals or private story moments.

Allowed placements:

- app launch after authentication, before joining a session
- session end screen
- between screens outside live play
- passive player screens such as notes or quest log
- optional rewarded ad for temporary ad-free session time

Forbidden placements:

- immediately before showing a private message
- immediately after a DM reveal
- during dice result display
- during active combat/initiative screen
- blocking a player from reading content that was already revealed
- while the host is actively controlling a live scene

## Reason

The product's value is immersion and trust. Ads that break live play will hurt retention and reviews.
