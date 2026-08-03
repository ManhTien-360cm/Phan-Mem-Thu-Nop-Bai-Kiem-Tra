# verify-public-submission-archive

The authenticated Student calls this function after the insert-only Storage
upload. The function reads the one server-declared object with a service-role
credential, enforces the 10 MiB size, validates ZIP/RAR/7Z magic and SHA-256,
marks `archive_signature_verified`, then finalizes through the Student RPC
context. MIME is not used as proof of archive type.
