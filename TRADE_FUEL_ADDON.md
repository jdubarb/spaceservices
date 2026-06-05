# Space Services: Trade and Fuel

This addon is planned as the trade-stop and commodity-sale layer for Space Services.

## Direction

The addon should not integrate Rimsential Spaceports or Trader Ships directly. Previous Spaceports testing showed too much fragile shuttle and lord state. Trade and Fuel should use Space Services' own predictable arrival, reservation, and departure model.

The basic loop:

1. A commercial visitor reserves a suitable Space Services pad.
2. A visual shuttle lands.
3. A trader pawn disembarks and can be traded with.
4. The visit lasts a short fixed window unless dismissed or blocked by danger.
5. Fuel or commodity sales are handled by Space Services-owned buildings and transactions.
6. The trader boards and leaves through the service pad.

## First Target

Chemfuel sales should be the first commodity because it is universal and easy to test.

Open decisions:

- Whether fuel is pulled from a dedicated tank, adjacent pump, stockpiles, or a hybrid.
- Whether fuel stops always include a trader pawn or can be fuel-only.
- How much goodwill, if any, should attach to successful fuel service.

## DBH Water

Dubs Bad Hygiene water sales are planned as a later optional integration.

Current balance target:

- `0.20` silver per liter.
- Up to `500 L` per normal stop.
- Maximum normal payout around `100` silver.

## VGE Astrofuel

Vanilla Gravship Expanded astrofuel is planned as a later optional premium fuel tier.

Likely behavior:

- Active only when VGE is loaded.
- Lower requested volume than chemfuel.
- Higher payout per unit.
- Potentially tied to advanced ship visitor types.

## Larger Commercial Pads

A larger pad may make sense later for trade ships and bus-style shuttles. Large commercial ships should probably require exposed/outdoor pad access, with passengers or traders handled through sealed nearby facilities if needed.
