-- Issue #743 (epic #726): catalog-declared sudo policy for ssh-transport components.
--
-- The sibling repository's catalog declares, PER COMPONENT, whether the InSpec
-- profile must run under sudo and whether that sudo prompts for a password
-- (module.transport.ssh.ps1 reads `$Comp.sudo` / `$Comp.sudoRequiresPassword` from
-- settings/catalog.json). Waypoint's execution path previously hard-coded
-- SudoRequiresPassword=true and sourced the --sudo decision from the resolved
-- credential's sudo_enabled flag alone, which cannot express the documented
-- per-product shapes (vIDM: sudo with password; Photon: passwordless sudo; the Aria
-- family: no sudo at all). Sudo policy is CONTENT knowledge -- it belongs on the
-- catalog component, frozen into the immutable plan item at plan-compile time, never
-- guessed from the target kind or the credential row.
--
-- Two additive column pairs:
--   * catalog_components.requires_sudo / sudo_requires_password -- the catalog
--     policy. Defaults (false, true) mirror the sibling catalog's own defaults:
--     components that never declared sudo run without it, and a component that
--     enables sudo without saying otherwise is assumed to need the password.
--   * scan_plan_items.requires_sudo / sudo_requires_password -- the plan-time
--     freeze (ADR-0023 immutable plans). NULLABLE: NULL means "row predates this
--     migration" and execution falls back to the pre-#743 credential-driven
--     behavior; new rows always carry the frozen catalog values.
--
-- Seed reconciliation below re-states docs/compliance-parity.md's documented sudo
-- shapes for the rows migrations 0064/0067 seeded (immutable -- never edited in
-- place). Values are the sibling catalog's own, restated as data:
--   photon/photon               sudo, passwordless
--   vidm/vidm                   sudo, password required
--   vcf/sddc-manager-*          sudo, password required
--   everything else             no sudo (defaults)
-- Imported (content-pull) components take the column defaults; deriving sudo policy
-- at promotion time from vendor content is follow-up work on the importer lane.
--
-- Idempotent and replay-safe: ADD COLUMN IF NOT EXISTS plus absolute UPDATEs keyed
-- on natural keys (safe to run any number of times, including after 0064/0067
-- replays re-insert rows under the declared-scope keys 0070 reconciled).

ALTER TABLE catalog_components
    ADD COLUMN IF NOT EXISTS requires_sudo boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS sudo_requires_password boolean NOT NULL DEFAULT true;

ALTER TABLE scan_plan_items
    ADD COLUMN IF NOT EXISTS requires_sudo boolean,
    ADD COLUMN IF NOT EXISTS sudo_requires_password boolean;

-- Photon OS whole-appliance SRG: sudo enabled, passwordless (sibling catalog
-- photon/5-0 `sudo: true, sudoRequiresPassword: false`).
UPDATE catalog_components cc
SET requires_sudo = true, sudo_requires_password = false
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
WHERE cc.product_version_id = pv.id
  AND p.product_key = 'photon'
  AND cc.component_key = 'photon'
  AND cc.transport = 'ssh';

-- Workspace ONE Access (vIDM) whole-appliance SRG: sudo enabled, password required
-- (sibling catalog vidm/3-3-x `sudo: true, sudoRequiresPassword: true`).
UPDATE catalog_components cc
SET requires_sudo = true, sudo_requires_password = true
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
WHERE cc.product_version_id = pv.id
  AND p.product_key = 'vidm'
  AND cc.component_key = 'vidm'
  AND cc.transport = 'ssh';

-- VCF 9.x SDDC Manager ssh services: the sibling catalog's sddcmgr components run as
-- the vcf-user account and declare `sudo: true, sudoRequiresPassword: true`; every
-- other VCF ssh component (operations-*, operations-hcx-*, operations-networks-*)
-- declares no sudo and keeps the defaults.
UPDATE catalog_components cc
SET requires_sudo = true, sudo_requires_password = true
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
WHERE cc.product_version_id = pv.id
  AND p.product_key = 'vcf'
  AND cc.component_key IN ('sddc-manager-nginx', 'sddc-manager-postgresql', 'sddc-manager-photon')
  AND cc.transport = 'ssh';
