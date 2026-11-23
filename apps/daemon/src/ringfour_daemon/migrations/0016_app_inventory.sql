ALTER TABLE app_inventory_observation ADD COLUMN last_successful_json TEXT
CHECK (last_successful_json IS NULL OR json_valid(last_successful_json));
