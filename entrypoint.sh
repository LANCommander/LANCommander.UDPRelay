#!/bin/sh
set -e

# Detect the docker socket's group GID at runtime
DOCKER_SOCK="${DOCKER_ENDPOINT:-/var/run/docker.sock}"
# Remove unix:// prefix if present
DOCKER_SOCK="${DOCKER_SOCK#unix://}"

if [ -e "$DOCKER_SOCK" ]; then
    # Get the GID of the docker socket (works on both Linux stat variants)
    SOCKET_GID=$(stat -c '%g' "$DOCKER_SOCK" 2>/dev/null || stat -f '%g' "$DOCKER_SOCK" 2>/dev/null || echo "")
    
    if [ -n "$SOCKET_GID" ] && [ "$SOCKET_GID" != "0" ]; then
        # Find existing group with this GID (might be named 'docker' or something else)
        EXISTING_GROUP=$(getent group "$SOCKET_GID" | cut -d: -f1)
        
        if [ -z "$EXISTING_GROUP" ]; then
            # No group exists with this GID, create docker group
            groupadd -g "$SOCKET_GID" docker 2>/dev/null || true
            EXISTING_GROUP="docker"
        fi
        
        # Ensure udprelay user is in the group with matching GID
        if ! id -nG udprelay 2>/dev/null | grep -qw "$EXISTING_GROUP"; then
            # Add user to the group
            usermod -a -G "$EXISTING_GROUP" udprelay 2>/dev/null || {
                # Fallback: manually add to /etc/group if usermod fails
                # This handles cases where usermod might not work in some environments
                if [ -w /etc/group ]; then
                    # Add udprelay to the group, handling both cases: with and without existing users
                    # Pattern: group:passwd:gid:users -> group:passwd:gid:users,udprelay
                    # or group:passwd:gid: -> group:passwd:gid:udprelay
                    if grep -q "^${EXISTING_GROUP}:[^:]*:[^:]*:$" /etc/group; then
                        # Group has no users (ends with :), add udprelay directly
                        sed -i "/^${EXISTING_GROUP}:/s/:$/:udprelay/" /etc/group 2>/dev/null || true
                    else
                        # Group has users, append ,udprelay
                        sed -i "/^${EXISTING_GROUP}:/s/$/,udprelay/" /etc/group 2>/dev/null || true
                    fi
                fi
            }
        fi
    fi
fi

# Switch to non-root user and run the application
# Use gosu if available, otherwise fall back to su
if command -v gosu >/dev/null 2>&1; then
    exec gosu udprelay "$@"
else
    # Fallback to su (less ideal but works)
    exec su -s /bin/sh udprelay -c "exec \"\$@\"" -- "$@"
fi
