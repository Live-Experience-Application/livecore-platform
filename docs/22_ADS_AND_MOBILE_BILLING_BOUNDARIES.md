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
