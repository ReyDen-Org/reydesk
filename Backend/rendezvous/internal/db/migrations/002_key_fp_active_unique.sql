-- A revoked row stays forever (an ID is never reissued) but must not block the
-- same key from registering again after "Reset ReyDesk ID". Uniqueness of the
-- key therefore only applies to active (not revoked) rows.
ALTER TABLE devices DROP CONSTRAINT IF EXISTS devices_key_fp_key;
CREATE UNIQUE INDEX IF NOT EXISTS devices_key_fp_active_idx ON devices (key_fp) WHERE revoked_at IS NULL;
