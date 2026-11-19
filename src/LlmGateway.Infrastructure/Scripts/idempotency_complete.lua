-- KEYS[1] = tenant-scoped chat idempotency hash
-- ARGV: fingerprint, HTTP status, content type, serialized body, retention TTL seconds
local status = redis.call("HGET", KEYS[1], "status")
local fingerprint = redis.call("HGET", KEYS[1], "request_fingerprint")

if status ~= "in_progress" or fingerprint ~= ARGV[1] then
    return 0
end

redis.call("HSET", KEYS[1],
    "status", "completed",
    "status_code", ARGV[2],
    "content_type", ARGV[3],
    "response_body", ARGV[4])
redis.call("EXPIRE", KEYS[1], tonumber(ARGV[5]))
return 1
