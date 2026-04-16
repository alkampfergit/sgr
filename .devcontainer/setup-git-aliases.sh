#!/bin/bash
# Setup git aliases for the development container

set -euo pipefail

echo "Setting up git aliases..."

default_branch_ref="$(git symbolic-ref --quiet refs/remotes/origin/HEAD 2>/dev/null || true)"
if [ -n "$default_branch_ref" ]; then
  default_branch="${default_branch_ref#refs/remotes/origin/}"
else
  default_branch="main"
fi

# Log aliases
git config --global alias.logf 'log --graph --oneline --all --decorate'
git config --global alias.logr 'log --graph --oneline --decorate'

# Branch status alias
git config --global alias.bstatus "!f() { branch=\${1:-$default_branch}; git log HEAD...origin/\$branch --oneline --left-right | awk '{ print substr(\$0, 1, 1)}' | sort -n | uniq -c; }; f"

# Rebase and fetch aliases
git config --global alias.rod "!f() { branch=\${1:-$default_branch}; git fetch --prune && git rebase origin/\$branch; }; f"
git config --global alias.fpull '!git fetch --prune && git pull --rebase'
git config --global alias.rfi "!f() { branch=\${1:-$default_branch}; git rebase -i \$(git merge-base HEAD origin/\$branch); }; f"
git config --global alias.rc 'rebase --continue'

# Commit aliases
git config --global alias.amen '!git commit -a --amend -C HEAD'
git config --global alias.pamen '!git commit -a --amend -C HEAD && git push --force-with-lease'

# Push aliases
git config --global alias.pushf 'push --force-with-lease'

echo "Git aliases configured successfully!"
