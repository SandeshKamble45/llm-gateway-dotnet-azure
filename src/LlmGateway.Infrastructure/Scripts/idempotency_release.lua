-- KEYS[1] = tenant-scoped chat idempotency hash
-- ARGV[1] = request fingerprint
local status = redis.call("HGET", KEYS[1], "status")
local fingerprint = redis.call("HGET", KEYS[1], "request_fingerprint")

if status == "in_progress" and fingerprint == ARGV[1] then
    return redis.call("DEL", KEYS[1])
end

return 0
