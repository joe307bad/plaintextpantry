#!/usr/bin/env bash
# Who has signed in to production, and how often:
#
#   ./user-activity.sh        every sign-in there is a record of
#   ./user-activity.sh 7      only the last 7 days
#
# Runs its namesake, `infra/deploy/user-activity.sh`, on the EC2 box through
# SSM Run Command - no SSH and no session-manager plugin - and prints what it
# said. That script holds the query and explains the columns; the records
# themselves are Keycloak's login events, which only exist from the deploy
# that turned them on. Needs nothing but the AWS credentials a deploy uses.
set -euo pipefail

days="${1:-}"
[ -z "$days" ] || [ "$days" -gt 0 ] 2>/dev/null || { echo "usage: $0 [days]" >&2; exit 1; }

# By tag rather than by instance id: the box is cattle, and a replacement gets
# a new id but the same name.
id=$(aws ssm send-command \
  --targets Key=tag:Name,Values=plaintextpantry \
  --document-name AWS-RunShellScript \
  --parameters "commands=[\"/opt/plaintextpantry/user-activity.sh $days\"]" \
  --query Command.CommandId --output text)

# Run Command is asynchronous, and for a second or two after sending there is
# no invocation to ask about yet - "None" is that, not a verdict.
status=Pending
for _ in $(seq 1 30); do
  status=$(aws ssm list-command-invocations --command-id "$id" --query 'CommandInvocations[0].Status' --output text)
  case "$status" in Pending | InProgress | None | "") sleep 2 ;; *) break ;; esac
done

aws ssm list-command-invocations --command-id "$id" --details \
  --query 'CommandInvocations[0].CommandPlugins[0].Output' --output text

[ "$status" = Success ] || { echo "run command: $status" >&2; exit 1; }
