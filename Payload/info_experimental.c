/*
 * EXPERIMENTAL payload network side. See struct_experimental.h and
 * PROTOCOL notes below. The default payload (info.c) is unchanged.
 *
 * Every connection: u32 cmd
 *   0 (CMD_EXIT)       -> payload exits
 *   1 (CMD_PACKAGE)    -> [u32 len+bytes] URL, Name, ContentID, Type, u64 size,
 *                         [u32 len+bytes] icon            (legacy, unchanged)
 *   2 (CMD_PACKAGE_V2) -> same as 1, then i32 storage (-1 default, 0 internal, 1 extended)
 *   3 (CMD_FREE_SPACE) -> payload writes u64 internal_free, internal_total,
 *                         extended_free, extended_total (little endian), closes
 * All integers little endian. String lengths exclude any NUL and must be
 * smaller than the payload buffer (url 0x800, name 0x259, id 0x30, type 0x10).
 */
#include <sys/types.h>
#include <sys/param.h>
#include <sys/mount.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <unistd.h>
#include <fcntl.h>
#include "struct_experimental.h"

struct
{
	uint32_t addr;
	uint16_t port;

} __attribute__((packed)) volatile Connection = { 0xb4b4b4b4, 0xb4b4 };

/* 0 when exactly len bytes were read, -1 on EOF/error */
static int read_full(int sock, void* buf, size_t len) {
	char* p = (char*)buf;
	while (len > 0) {
		ssize_t n = read(sock, p, len);
		if (n <= 0)
			return -1;
		p += n;
		len -= (size_t)n;
	}
	return 0;
}

static int write_full(int fd, const void* buf, size_t len) {
	const char* p = (const char*)buf;
	while (len > 0) {
		ssize_t n = write(fd, p, len);
		if (n <= 0)
			return -1;
		p += n;
		len -= (size_t)n;
	}
	return 0;
}

static int get_uint32(int sock, uint32_t* out) {
	return read_full(sock, out, sizeof(*out));
}

static int get_uint64(int sock, uint64_t* out) {
	return read_full(sock, out, sizeof(*out));
}

static int str_len(const char* s) {
	int n = 0;
	while (s[n] != 0)
		n++;
	return n;
}

static int str_contains(const char* s, const char* needle) {
	for (int i = 0; s[i] != 0; i++) {
		int j = 0;
		while (needle[j] != 0 && s[i + j] == needle[j])
			j++;
		if (needle[j] == 0)
			return 1;
	}
	return 0;
}

/* bounded append, always NUL terminated */
static void append_string(char* buffer, int size, const char* new_content) {
	int len = str_len(buffer);
	for (int i = 0; new_content[i] != 0 && len < size - 1; i++)
		buffer[len++] = new_content[i];
	buffer[len] = 0;
}

/* reads [u32 len + bytes] into Buffer, NUL terminated; -1 if too long or short read */
static int get_string(int sock, char* Buffer, int MAX_SIZE) {
	uint32_t len;
	Buffer[0] = 0;
	if (get_uint32(sock, &len) != 0)
		return -1;
	if (len >= (uint32_t)MAX_SIZE)
		return -1;
	if (read_full(sock, Buffer, len) != 0) {
		Buffer[0] = 0;
		return -1;
	}
	Buffer[len] = 0;
	return 0;
}

/* discards len bytes from the socket */
static int drain(int sock, uint32_t len, char* io, int io_size) {
	while (len > 0) {
		uint32_t chunk = len < (uint32_t)io_size ? len : (uint32_t)io_size;
		if (read_full(sock, io, chunk) != 0)
			return -1;
		len -= chunk;
	}
	return 0;
}

/*
 * Reads [u32 len + bytes] icon and stores it at /user/data/tmp_<name>.
 * out_path is "" when there is no icon or it could not be written.
 * The bytes are always consumed so later fields stay in sync.
 * Returns -1 only on a socket error.
 */
static int get_file(int sock, char* out_path, int out_size, const char* name, char* io, int io_size) {
	uint32_t len;
	out_path[0] = 0;
	if (get_uint32(sock, &len) != 0)
		return -1;
	if (len == 0)
		return 0;

	append_string(out_path, out_size, "/user/data/tmp_");
	append_string(out_path, out_size, name);

	int hFile = open(out_path, O_WRONLY | O_CREAT | O_TRUNC, 0777);
	if (hFile < 0) {
		out_path[0] = 0;
		return drain(sock, len, io, io_size);
	}

	int write_ok = 1;
	while (len > 0) {
		uint32_t chunk = len < (uint32_t)io_size ? len : (uint32_t)io_size;
		if (read_full(sock, io, chunk) != 0) {
			close(hFile);
			unlink(out_path);
			out_path[0] = 0;
			return -1;
		}
		len -= chunk;
		if (write_ok && write_full(hFile, io, chunk) != 0)
			write_ok = 0; /* keep draining the socket */
	}

	close(hFile);
	if (!write_ok) {
		unlink(out_path);
		out_path[0] = 0;
	}
	return 0;
}

/* free/total bytes of the filesystem at path; 0/0 on failure */
static void fs_space(const char* path, const char* require_mount, uint64_t* free_bytes, uint64_t* total_bytes) {
	struct statfs st;
	*free_bytes = 0;
	*total_bytes = 0;

	char* p = (char*)&st;
	for (unsigned i = 0; i < sizeof(st); i++)
		p[i] = 0;

	if (statfs(path, &st) != 0)
		return;

	/* an unmounted /mnt/ext0 would report the parent filesystem */
	if (require_mount) {
		st.f_mntonname[sizeof(st.f_mntonname) - 1] = 0;
		if (!str_contains(st.f_mntonname, require_mount))
			return;
	}

	*total_bytes = st.f_blocks * st.f_bsize;
	*free_bytes = st.f_bavail > 0 ? (uint64_t)st.f_bavail * st.f_bsize : 0;
}

static int send_free_space(int sock) {
	uint64_t reply[4];
	fs_space("/user", 0, &reply[0], &reply[1]);
	fs_space("/mnt/ext0", "ext0", &reply[2], &reply[3]);
	return write_full(sock, reply, sizeof(reply));
}

#define REGLOC_FILE "/user/data/dpi_install_location"

void remember_location(int value) {
	int fd = open(REGLOC_FILE, O_WRONLY | O_CREAT | O_TRUNC, 0666);
	if (fd >= 0) {
		write_full(fd, &value, sizeof(value));
		close(fd);
	}
}

int pending_location(int* value) {
	int fd = open(REGLOC_FILE, O_RDONLY, 0);
	if (fd < 0)
		return 0;
	int ok = read_full(fd, value, sizeof(*value)) == 0;
	close(fd);
	return ok;
}

void forget_location(void) {
	unlink(REGLOC_FILE);
}

void send_result(struct pkg_buffers* b, int rv, int task)
{
	if (b->reply_sock < 0)
		return;
	int32_t reply[2] = { rv, task };
	write_full(b->reply_sock, reply, sizeof(reply));
	close(b->reply_sock);
	b->reply_sock = -1;
}

/*
 * A malformed or cut-off request only drops that connection (INFO_HANDLED):
 * the payload exits on CMD_EXIT or when the PC can't be reached at all.
 */
int get_pkg_info(struct bgft_download_param* params, struct pkg_buffers* b, int* storage)
{
	*storage = STORAGE_DEFAULT;
	b->reply_sock = -1;

	struct sockaddr_in conn_info = {
		.sin_family = AF_INET,
		.sin_addr = {.s_addr = Connection.addr},
		.sin_port = Connection.port,
	};

	int sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (sock < 0)
		return INFO_ERROR;

	if (connect(sock, (struct sockaddr*)&conn_info, sizeof(conn_info)) < 0) {
		close(sock);
		return INFO_ERROR;
	}

	uint32_t cmd;
	if (get_uint32(sock, &cmd) != 0) {
		close(sock);
		return INFO_HANDLED;
	}

	if (cmd == CMD_EXIT) {
		close(sock);
		return INFO_EXIT;
	}

	if (cmd == CMD_FREE_SPACE) {
		send_free_space(sock); /* a failed reply is the PC's problem; keep serving */
		close(sock);
		return INFO_HANDLED;
	}

	if (cmd != CMD_PACKAGE && cmd != CMD_PACKAGE_V2 && cmd != CMD_PACKAGE_V3) {
		close(sock);
		return INFO_HANDLED;
	}

	/* Order: URL, Name, ID, Type, size, [Icon Len, Icon Data], (v2) storage */
	uint64_t size = 0;
	if (get_string(sock, b->url, sizeof(b->url)) != 0 ||
		get_string(sock, b->name, sizeof(b->name)) != 0 ||
		get_string(sock, b->id, sizeof(b->id)) != 0 ||
		get_string(sock, b->pkg_type, sizeof(b->pkg_type)) != 0 ||
		get_uint64(sock, &size) != 0) {
		close(sock);
		return INFO_HANDLED;
	}

	/* icon file name from the content id; never let it escape /user/data */
	b->icon_name[0] = 0;
	append_string(b->icon_name, sizeof(b->icon_name), b->id);
	for (int i = 0; b->icon_name[i] != 0; i++)
		if (b->icon_name[i] == '/' || b->icon_name[i] == '.')
			b->icon_name[i] = '_';
	append_string(b->icon_name, sizeof(b->icon_name), ".png");

	if (get_file(sock, b->icon_path, sizeof(b->icon_path), b->icon_name, b->io, sizeof(b->io)) != 0) {
		close(sock);
		return INFO_HANDLED;
	}

	if (cmd == CMD_PACKAGE_V2 || cmd == CMD_PACKAGE_V3) {
		int32_t st;
		if (read_full(sock, &st, sizeof(st)) != 0) {
			close(sock);
			return INFO_HANDLED;
		}
		*storage = st;
	}

	if (cmd == CMD_PACKAGE_V3)
		b->reply_sock = sock;  /* main() answers with send_result() */
	else
		close(sock);

	params->id = b->id;
	params->content_url = b->url;
	params->content_name = b->name;
	params->package_size = size;
	params->icon_path = b->icon_path;
	params->package_type = b->pkg_type;

	return INFO_PACKAGE;
}
