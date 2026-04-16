#!/bin/bash
# Post-creation setup for the sgr development container.

set -euo pipefail

echo "=========================================="
echo "Starting devcontainer post-creation setup"
echo "=========================================="

# Fix apt sources issue with Yarn
echo "Cleaning up apt sources..."
sudo rm -f /etc/apt/sources.list.d/yarn.list

# Update apt and install git-flow
echo "Installing system dependencies..."
sudo apt-get update
sudo apt-get install -y git-flow

# Setup git aliases
echo "Configuring git aliases..."
bash .devcontainer/setup-git-aliases.sh

# Install CLI tools distributed via npm
if command -v npm >/dev/null 2>&1; then
    echo "Installing automata-cli..."
    npm install -g automata-cli || true

    echo "Installing OpenAI Codex..."
    npm install -g @openai/codex || true
else
    echo "npm not available, skipping npm-based CLI installs."
fi

# Install Claude Code via the official installer
echo "Installing Claude Code CLI..."
curl -fsSL https://claude.ai/install.sh | bash || true

# Install uv and GitHub spec-kit
if ! command -v uv >/dev/null 2>&1; then
    echo "Installing uv..."
    curl -LsSf https://astral.sh/uv/install.sh | sh
else
    echo "uv already installed, skipping."
fi

if command -v uv >/dev/null 2>&1; then
    echo "Installing github spec-kit via uv..."
    uv tool install specify-cli --from git+https://github.com/github/spec-kit.git || true
else
    echo "uv not available, cannot install spec-kit."
fi

# Install tokensave and configure agent integrations
echo "Installing tokensave..."
TOKENSAVE_TAG=$(curl -sI https://github.com/aovestdipaperino/tokensave/releases/latest | grep -i '^location:' | sed 's|.*/tag/||;s/\r//')
TOKENSAVE_VERSION="${TOKENSAVE_TAG#v}"
ARCH=$(uname -m)
if [ "$ARCH" = "aarch64" ] || [ "$ARCH" = "arm64" ]; then
    TOKENSAVE_ARCH="aarch64-linux"
else
    TOKENSAVE_ARCH="x86_64-linux"
fi
TOKENSAVE_URL="https://github.com/aovestdipaperino/tokensave/releases/download/${TOKENSAVE_TAG}/tokensave-${TOKENSAVE_TAG}-${TOKENSAVE_ARCH}.tar.gz"
echo "  Downloading tokensave ${TOKENSAVE_VERSION} (${TOKENSAVE_ARCH})..."
curl -sL "$TOKENSAVE_URL" -o /tmp/tokensave.tar.gz || true
if [ -f /tmp/tokensave.tar.gz ]; then
    tar xzf /tmp/tokensave.tar.gz -C /tmp || true
    if [ -f /tmp/tokensave ]; then
        sudo mv /tmp/tokensave /usr/local/bin/tokensave
        echo "  tokensave installed."
    fi
    rm -f /tmp/tokensave.tar.gz
fi

if command -v tokensave >/dev/null 2>&1; then
    echo "  Configuring tokensave for Claude Code..."
    tokensave install --agent claude || true
    echo "  Configuring tokensave for Codex CLI..."
    tokensave install --agent codex || true
    tokensave enable-upload-counter || true
    echo "  Indexing repository..."
    tokensave sync || true
else
    echo "tokensave install failed or is unavailable, skipping configuration."
fi

# Install Homebrew and rtk
append_if_missing() {
    local line="$1"
    local file="$2"

    touch "$file"
    if ! grep -Fqx "$line" "$file"; then
        echo "$line" >>"$file"
    fi
}

if ! command -v brew >/dev/null 2>&1; then
    echo "Installing Homebrew..."
    NONINTERACTIVE=1 bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)" || true
else
    echo "Homebrew already installed, skipping."
fi

if [ -x /home/linuxbrew/.linuxbrew/bin/brew ]; then
    BREW_BIN="/home/linuxbrew/.linuxbrew/bin/brew"
elif [ -x /opt/homebrew/bin/brew ]; then
    BREW_BIN="/opt/homebrew/bin/brew"
else
    BREW_BIN=""
fi

if [ -n "$BREW_BIN" ]; then
    BREW_SHELLENV_LINE="eval \"\$($BREW_BIN shellenv)\""
    append_if_missing "$BREW_SHELLENV_LINE" "$HOME/.zprofile"
    append_if_missing "$BREW_SHELLENV_LINE" "$HOME/.zshrc"
    append_if_missing "$BREW_SHELLENV_LINE" "$HOME/.bashrc"
    eval "$("$BREW_BIN" shellenv)"

    if brew list --formula rtk >/dev/null 2>&1; then
        echo "rtk already installed, skipping."
    else
        echo "Installing rtk via Homebrew..."
        brew install rtk || true
    fi

    if command -v rtk >/dev/null 2>&1; then
        echo "Configuring rtk for Claude Code..."
        rtk init --global --auto-patch || true
        echo "Configuring rtk for Codex CLI..."
        rtk init --global --codex --auto-patch || true
    else
        echo "rtk install failed or is unavailable, skipping configuration."
    fi
else
    echo "Homebrew install did not expose brew on a known path, skipping rtk."
fi
