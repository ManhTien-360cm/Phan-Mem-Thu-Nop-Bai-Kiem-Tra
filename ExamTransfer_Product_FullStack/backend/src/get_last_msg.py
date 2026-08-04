import json
with open(r'C:\Users\Admin\.gemini\antigravity\brain\2986b572-039a-4c7f-b1cd-2ffd9fbacc51\.system_generated\logs\transcript.jsonl', 'r', encoding='utf-8') as f:
    lines = f.readlines()
for line in reversed(lines):
    if '\"type\":\"USER_INPUT\"' in line:
        with open(r'D:\last_user_message.txt', 'w', encoding='utf-8') as out_f:
            out_f.write(line)
        break
