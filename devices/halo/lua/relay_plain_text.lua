-- Plain-text display script for Halo using Brilliant TxPlainText.
-- Shows brief user-facing state and findings only (no decision logs).

local last_title = ""
local last_body = ""

function relay_show(title, body)
  last_title = string.sub(title or "", 1, 40)
  last_body = string.sub(body or "", 1, 192)
  -- TxPlainText is sent by the host bridge; this file documents the on-device contract.
end

function relay_clear()
  last_title = ""
  last_body = ""
end
