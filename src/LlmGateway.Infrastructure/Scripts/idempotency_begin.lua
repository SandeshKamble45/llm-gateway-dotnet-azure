-- KEYS[1] = tenant-scoped chat idempotency hash
-- ARGV[1] = request fingerprint; ARGV[2] = retention TTL seconds
local fingerprint = ARGV[1]
local ttl = tonumber(ARGV[2])
local status = redis.call("HGET", KEYS[1], "status")

if not status then
    redis.call("HSET", KEYS[1],
        "status", "in_progress",
        "request_fingerprint", fingerprint)
    redis.call("EXPIRE", KEYS[1], ttl)
    return { 1 }
end

local existing_fingerprint = redis.call("HGET", KEYS[1], "request_fingerprint")
if existing_fingerprint ~= fingerprint then
    return { 2 }
end

if status == "completed" then
    return {
        4,
        tonumber(redis.call("HGET", KEYS[1], "status_code")),
        redis.call("HGET", KEYS[1], "content_type"),
        redis.call("HGET", KEYS[1], "response_body")
    }
end

return { 3 }
