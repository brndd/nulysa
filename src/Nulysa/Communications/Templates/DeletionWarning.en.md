Subject: {{association}} — Membership ended: account deletion warning

Login and member services are blocked immediately. Your account becomes eligible for deletion on {{deletion_eligible_at}}. Batches run every 14 days: deletion is normally {{deletion_window_start}}–{{deletion_window_end}}, possibly later after failures. Contact an administrator to request restoration before actual deletion.

{% if unspent_funds %}
Unspent funds: {{unspent_funds}}. Contact the treasurer for a refund. Scheduled account deletion does not forfeit your claim.
{% endif %}
