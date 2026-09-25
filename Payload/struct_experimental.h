#ifndef DEFPARAMS_EXPERIMENTAL
#define DEFPARAMS_EXPERIMENTAL

/*
 * EXPERIMENTAL payload (payload_experimental.bin). The default payload
 * (main.c / info.c / struct.h -> payload.bin) is unchanged.
 */

/* like struct.h: include after <sys/types.h> or ps4-libjbc/jailbreak.h */

enum bgft_task_option_t {
	BGFT_TASK_OPTION_NONE = 0x0,
	BGFT_TASK_OPTION_DELETE_AFTER_UPLOAD = 0x1,
	BGFT_TASK_OPTION_INVISIBLE = 0x2,
	BGFT_TASK_OPTION_ENABLE_PLAYGO = 0x4,
	BGFT_TASK_OPTION_FORCE_UPDATE = 0x8,
	BGFT_TASK_OPTION_REMOTE = 0x10,
	BGFT_TASK_OPTION_COPY_CRASH_REPORT_FILES = 0x20,
	BGFT_TASK_OPTION_DISABLE_INSERT_POPUP = 0x40,
	BGFT_TASK_OPTION_DISABLE_CDN_QUERY_PARAM = 0x10000,
};

struct bgft_download_param {
	int user_id;
	int entitlement_type;
	const char* id;
	const char* content_url;
	const char* content_ex_url;
	const char* content_name;
	const char* icon_path;
	const char* sku_id;
	enum bgft_task_option_t option;
	const char* playgo_scenario_id;
	const char* release_date;
	const char* package_type;
	const char* package_sub_type;
	unsigned long package_size;
};

/* flatz ps4_stub_lib_maker_v2 bgft.h: SceBgftDownloadParamEx */
struct bgft_download_param_ex {
	struct bgft_download_param param;
	unsigned int slot;
};

/* Wire commands (PC -> PS4, first u32 of every connection) */
#define CMD_EXIT        0
#define CMD_PACKAGE     1  /* legacy package request */
#define CMD_PACKAGE_V2  2  /* package request + trailing i32 storage */
#define CMD_FREE_SPACE  3  /* reply: 4 x u64, then close */

/* storage values of CMD_PACKAGE_V2 */
#define STORAGE_DEFAULT  (-1)
#define STORAGE_INTERNAL 0
#define STORAGE_EXTENDED 1

/* get_pkg_info results */
#define INFO_ERROR   (-1)
#define INFO_PACKAGE 0
#define INFO_EXIT    1
#define INFO_HANDLED 2  /* request served on the socket (e.g. free space); loop again */

/* Buffer sizes match PayloadService.cs MaxUrl/MaxName/MaxId/MaxType (incl. NUL). */
struct pkg_buffers {
	char url[0x800];
	char name[0x259];
	char id[0x30];
	char pkg_type[0x10];
	char icon_name[0x40];
	char icon_path[0x100];
	char io[4096];
};

/*
 * Buffers live in main()'s frame (not .bss: objcopy -O binary drops the
 * trailing .bss, so large statics might not be backed by loaded memory),
 * so the pointers stored in params stay valid for the task registration.
 */
int get_pkg_info(struct bgft_download_param* params, struct pkg_buffers* bufs, int* storage);

#define BGFT_INVALID_TASK_ID (-1)
#define ORBIS_KERNEL_PRIO_FIFO_NORMAL  0x2BC

typedef struct OrbisUserServiceInitializeParams {
	uint32_t priority;
} OrbisUserServiceInitializeParams;

struct bgft_init_params {
	void* mem;
	unsigned long size;
};
void* dlopen(const char*, int);
void* dlsym(void*, const char*);

#endif
