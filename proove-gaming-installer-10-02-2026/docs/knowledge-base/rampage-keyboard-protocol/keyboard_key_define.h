#ifndef _KEYBOARD_KEY_DEFINE_H_
#define _KEYBOARD_KEY_DEFINE_H_
#include <stdint.h>

//==========================================================
//类型定义
//==========================================================
//键盘按键类型定义
#define		KB_TYPE				0x10
#define		KB_REPEAT			0X11
//多媒体、系统类型定义
#define 	CON_TYPE			0X20
#define 	SYS_TYPE			0X30
//鼠标类型定义
#define		MS_TYPE				0X40
#define		MS_SWING			0X41
#define		MS_WHEEL			0X42
#define		MS_FIRE				0X43
#define		MS_DPI				0X44
#define		MS_REPORT			0X45
//宏类型定义
#define		MACRO_1_TYPE		0X50 //宏按键类型
#define		MACRO_N_TYPE		0X51 //宏播放次数
#define		MACRO_GUN_TYPE		0X52 //枪宏
//功能类型定义
#define		FUNC_TYPE			0X60
#define		FUNC_TEXT			0X61
#define		FN_COM_TYPE		    0X70
//高级键类型定义
#define		ADV_DKS_TYPE	    0X80 //动态键程
#define		ADV_MT_TYPE		    0X81 //按住/单击
#define		ADV_TGL_TYPE		0X82 //切换开关
#define		ADV_SOCD_TYPE		0X83 //急停Snappy Tappy
#define		ADV_RS_TYPE		    0X84 //迅洁

//宏录制和播放和高级键相关定义
#define		MACRO_KB_GEN		0X01
#define		MACRO_KB_HOT		0X02
#define		MACRO_MS_KEY		0X03
#define		MACRO_MS_X			0X04
#define		MACRO_MS_Y			0X05
#define		MACRO_MS_SWING		0X06
#define		MACRO_MS_WHEEL		0X07

//DPI类
#define		DPI_ADD				0X01	//dpi+
#define		DPI_DEC				0X02	//dpi-
#define		DPI_LOOP			0X03	//DPI LOOP

//==========================================================
//Mouse_CODE_TAB
//==========================================================
#define		MS_LEFT			0X01
#define		MS_RIGHT		0X02
#define		MS_MIDDLE		0X04
#define		MS_K4			0X08
#define		MS_K5			0X10

//==========================================================
//KEY_CODE_TAB
//==========================================================
#define 	KEY_DEFAULT		0x00
#define 	KEY_A			0x04
#define 	KEY_B			0x05
#define 	KEY_C			0x06
#define 	KEY_D			0x07
#define 	KEY_E			0x08
#define 	KEY_F			0x09
#define 	KEY_G			0x0A
#define 	KEY_H			0x0B
#define 	KEY_I			0x0C
#define 	KEY_J			0x0d
#define 	KEY_K			0x0e
#define 	KEY_L			0x0f
#define 	KEY_M			0x10
#define 	KEY_N			0x11
#define 	KEY_O			0x12
#define 	KEY_P			0x13
#define 	KEY_Q			0x14
#define 	KEY_R			0x15
#define 	KEY_S			0x16
#define 	KEY_T			0x17
#define 	KEY_U			0x18
#define 	KEY_V			0x19
#define 	KEY_W			0x1a
#define 	KEY_X			0x1b
#define 	KEY_Y			0x1c
#define 	KEY_Z			0x1d

#define		KEY_1			0x1e
#define		KEY_2			0x1f
#define		KEY_3			0x20
#define		KEY_4			0x21
#define		KEY_5			0x22
#define		KEY_6			0x23
#define		KEY_7			0x24
#define		KEY_8			0x25
#define		KEY_9			0x26
#define		KEY_0			0x27
#define		KEY_ENTER		0x28
#define		KEY_ESC			0x29

#define		KEY_BACKSPACE	0x2a
#define 	KEY_TAB			0x2b
#define 	KEY_SPACE		0x2c
#define 	KEY_DEC			0x2d
#define 	KEY_ADD			0x2e
#define 	KEY_BRACKET_L	0x2f	// [{
#define 	KEY_BRACKET_R	0x30	// ]}
#define 	KEY_SPRIT		0x31	//
#define 	KEY_EUROPE		0x32    //
#define 	KEY_SEMICOLON	0x33	// ;:
#define 	KEY_QUOTES		0x34	// '"
#define 	KEY_OVERDOT		0x35	// `~
#define 	KEY_COMMA		0x36	// ,<
#define 	KEY_PERIOD		0x37	// .>
#define 	KEY_QUESTION	0x38	// /?
#define 	KEY_CAP			0x39

#define 	KEY_F1			0x3a
#define 	KEY_F2			0x3b
#define 	KEY_F3			0x3c
#define 	KEY_F4			0x3d
#define 	KEY_F5			0x3e
#define 	KEY_F6			0x3f
#define 	KEY_F7			0x40
#define 	KEY_F8			0x41
#define 	KEY_F9			0x42
#define 	KEY_F10			0x43
#define 	KEY_F11			0x44
#define 	KEY_F12			0x45

#define 	KEY_PRINT		0x46
#define 	KEY_SCROLL		0x47
#define 	KEY_PAUSE		0x48

#define 	KEY_INSERT		0x49
#define 	KEY_HOME		0x4a
#define 	KEY_PGUP		0x4b
#define 	KEY_DELETE		0x4c
#define 	KEY_END			0x4d
#define 	KEY_PGDN		0x4e

#define 	KEY_RIGHT		0x4f
#define 	KEY_LEFT		0x50
#define 	KEY_DOWN		0x51
#define 	KEY_UP			0x52

#define 	KEY_NUM_LOCK	0x53
#define 	KEY_NUM_SPRIT	0x54  // "/"
#define 	KEY_NUM_STAR	0x55  // "*"
#define 	KEY_NUM_DEC		0x56
#define 	KEY_NUM_ADD		0x57
#define 	KEY_NUM_ENTER	0x58
#define 	KEY_NUM_1		0x59
#define 	KEY_NUM_2		0x5a
#define 	KEY_NUM_3		0x5b
#define 	KEY_NUM_4		0x5c
#define 	KEY_NUM_5		0x5d
#define 	KEY_NUM_6		0x5e
#define 	KEY_NUM_7		0x5f
#define 	KEY_NUM_8		0x60
#define 	KEY_NUM_9		0x61
#define 	KEY_NUM_0		0x62
#define 	KEY_NUM_DEL		0x63
#define 	KEY_APP			0x65

#define 	KEY_F13			0x68
#define 	KEY_F14			0x69
#define 	KEY_F15			0x6a
#define 	KEY_F16			0x6b
#define 	KEY_F17			0x6c
#define 	KEY_F18			0x6d
#define 	KEY_F19			0x6e
#define 	KEY_F20			0x6f
#define 	KEY_F21			0x70
#define 	KEY_F22			0x71
#define 	KEY_F23			0x72
#define 	KEY_F24			0x73

#define 	KEY_K29			0x31
#define   	KEY_K42			0x32
#define 	KEY_K45			0x64
#define 	KEY_K107		0x85
#define  	KEY_K56			0x87
#define 	KEY_K14			0x89
#define 	KEY_K132		0x8a
#define 	KEY_K131		0x8b
#define 	KEY_K133		0x88
#define 	KEY_K151		0x90
#define 	KEY_K150		0x91

//====================================================
//热键
//====================================================
#define 	KEY_CTRL_L		0x01
#define		KEY_SHIFT_L		0x02
#define 	KEY_ALT_L		0x04
#define 	KEY_WIN_L		0x08
#define 	KEY_CTRL_R		0x10
#define		KEY_SHIFT_R		0x20
#define 	KEY_ALT_R		0x40
#define 	KEY_WIN_R		0x80

//====================================================
//系统按键
//====================================================
#define		KEY_POWER		0x01
#define 	KEY_SLEEP		0x02
#define		KEY_WAKEUP		0x04

//====================================================
//多媒体类
//====================================================
// enum consumer_usages {
//     // 15.5 Display Controls
//     SNAPSHOT               = 0x065,
//     BRIGHTNESS_UP          = 0x06F, // https://www.usb.org/sites/default/files/hutrr41_0.pdf
//     BRIGHTNESS_DOWN        = 0x070,
//     // 15.7 Transport Controls
//     TRANSPORT_RECORD       = 0x0B2,
//     TRANSPORT_FAST_FORWARD = 0x0B3,
//     TRANSPORT_REWIND       = 0x0B4,
//     TRANSPORT_NEXT_TRACK   = 0x0B5,
//     TRANSPORT_PREV_TRACK   = 0x0B6,
//     TRANSPORT_STOP         = 0x0B7,
//     TRANSPORT_EJECT        = 0x0B8,
//     TRANSPORT_RANDOM_PLAY  = 0x0B9,
//     TRANSPORT_STOP_EJECT   = 0x0CC,
//     TRANSPORT_PLAY_PAUSE   = 0x0CD,
//     // 15.9.1 Audio Controls - Volume
//     AUDIO_MUTE             = 0x0E2,
//     AUDIO_VOL_UP           = 0x0E9,
//     AUDIO_VOL_DOWN         = 0x0EA,
//     // 15.15 Application Launch Buttons
//     AL_CC_CONFIG           = 0x183,
//     AL_EMAIL               = 0x18A,
//     AL_CALCULATOR          = 0x192,
//     AL_LOCAL_BROWSER       = 0x194,
//     AL_LOCK                = 0x19E,
//     AL_CONTROL_PANEL       = 0x19F,
//     AL_ASSISTANT           = 0x1CB,
//     AL_KEYBOARD_LAYOUT     = 0x1AE,
//     // 15.16 Generic GUI Application Controls
//     AC_NEW                 = 0x201,
//     AC_OPEN                = 0x202,
//     AC_CLOSE               = 0x203,
//     AC_EXIT                = 0x204,
//     AC_MAXIMIZE            = 0x205,
//     AC_MINIMIZE            = 0x206,
//     AC_SAVE                = 0x207,
//     AC_PRINT               = 0x208,
//     AC_PROPERTIES          = 0x209,
//     AC_UNDO                = 0x21A,
//     AC_COPY                = 0x21B,
//     AC_CUT                 = 0x21C,
//     AC_PASTE               = 0x21D,
//     AC_SELECT_ALL          = 0x21E,
//     AC_FIND                = 0x21F,
//     AC_SEARCH              = 0x221,
//     AC_HOME                = 0x223,
//     AC_BACK                = 0x224,
//     AC_FORWARD             = 0x225,
//     AC_STOP                = 0x226,
//     AC_REFRESH             = 0x227,
//     AC_BOOKMARKS           = 0x22A,
//     AC_MISSION_CONTROL     = 0x29F,
//     AC_LAUNCHPAD           = 0x2A0
// };

#define		CONSUME_NEXT   		0X00B5
#define		CON_NEXT_L			0XB5
#define		CON_NEXT_H			0X00

#define		CONSUME_PREV		0X00B6
#define		CON_PREV_L			0XB6
#define		CON_PREV_H			0X00

#define		CONSUME_STOP		0X00B7
#define		CON_STOP_L			0XB7
#define		CON_STOP_H			0X00

#define		CONSUME_MUTE		0X00E2
#define		CON_MUTE_L			0XE2
#define		CON_MUTE_H			0X00

#define		CONSUME_VOL_UP		0X00E9
#define		CON_VOL_UP_L		0XE9
#define		CON_VOL_UP_H		0X00

#define		CONSUME_VOL_DOWN	0X00EA
#define		CON_VOL_DOWN_L		0XEA
#define		CON_VOL_DOWN_H		0X00

#define		CONSUME_PLAY		0X00CD
#define		CON_PLAY_L			0XCD
#define		CON_PLAY_H			0X00

#define		CONSUME_MEDIA		0X0183
#define		CON_MEDIA_L			0X83
#define		CON_MEDIA_H			0X01

#define		CONSUME_MAIL		0X018A
#define		CON_MAIL_L			0X8A
#define		CON_MAIL_H			0X01

#define		CONSUME_CALCULATOR	0X0192
#define		CON_CALCULATOR_L	0X92
#define		CON_CALCULATOR_H	0X01

#define		CONSUME_COMPUTER	0X0194
#define		CON_COMPUTER_L		0X94
#define		CON_COMPUTER_H		0X01

#define		CONSUME_SEARCH		0X0221
#define		CON_SEARCH_L		0X21
#define		CON_SEARCH_H		0X02

#define		CONSUME_WWW_HOME	0X0223
#define		CON_WWW_HOME_L		0X23
#define		CON_WWW_HOME_H		0X02

#define		CONSUME_WWW_BACK	0X0224
#define		CON_WWW_BACK_L		0X24
#define		CON_WWW_BACK_H		0X02

#define		CONSUME_WWW_FORWARD	0X0225
#define		CON_WWW_FORWARD_L	0X25
#define		CON_WWW_FORWARD_H	0X02

#define		CONSUME_WWW_STOP	0X0226
#define		CON_WWW_STOP_L		0X26
#define		CON_WWW_STOP_H		0X02

#define		CONSUME_WWW_REFRESH	0X0227
#define		CON_WWW_REFRESH_L	0X27
#define		CON_WWW_REFRESH_H	0X02

#define		CONSUME_WWW_COLLECT	0X022A
#define		CON_WWW_COLLECT_L	0X2A
#define		CON_WWW_COLLECT_H	0X02

#define		CONSUME_LIGHT_DOWN	0X0070
#define		CON_LIGHT_DOWN_L    0X70
#define		CON_LIGHT_DOWN_H    0x00

#define		CONSUME_LIGHT_UP	0X006F
#define		CON_LIGHT_UP_L      0X6F
#define		CON_LIGHT_UP_H      0X00

//MAC 多任务控制
#define		CONSUME_MAC_CONTROL	0X029F
#define		CON_MAC_CONTROL_L   0X9F
#define		CON_MAC_CONTROL_H   0X02

//MAC 启动台
#define		CONSUME_MAC_LAUNCHPAD   0X02A0
#define		CON_MAC_LAUNCHPAD_L     0XA0
#define		CON_MAC_LAUNCHPAD_H     0X02

//====================================================
//自定义功能	0x01 -- 0xff
//====================================================
/************ 0x01~0x1F *******************/
#define		KEY_FN					0x01 //FN键 
#define		KEY_MACRO				0X02 //按键宏

#define		KEY_FUNCTION			0X03 //电脑快捷键 
#define		KEY_WIN_LOCK			0x04 //锁WIN

#define	    KEY_RESET				0x05 //按键复位
#define     KEY_KEYBOARD_LOCK 		0X06 //锁键
#define		KEY_WASD_CHANGE   		0X07 //WASD改成方向键

/************ 0x20~0x4F *******************/
#define	    KEY_LIGHT_SET			0x20 //背光亮度设置
#define		KEY_LIGHT_UP			0X21 //背光亮度+
#define		KEY_LIGHT_DOWN			0X22 //背光亮度-

#define	    KEY_SPEED_SET			0x23 //背光速度设置
#define		KEY_SPEED_ADD			0X24 //背光速度+
#define		KEY_SPEED_DEC			0X25 //背光速度-

#define	    KEY_TREND_SET			0x26 //背光方向设置
#define   	KEY_LEFT_TREND    		0X27 //背光方向左
#define  	KEY_RIGHT_TREND   		0X28 //背光方向右

#define	    KEY_COLOUR_BOARD_SET	0X29 //色板设置颜色
#define		KEY_COLOUR_SET   		0X2A //七色+幻彩颜色循环切换

#define		KEY_LED_OFF				0X2B //背光关闭
#define		KEY_LED_SET				0X2C //背光模式循环切换
#define		KEY_LED_SET_1			0x2D //背光切换1
#define		KEY_LED_SET_2			0x2E //背光切换2
#define		KEY_LED_SET_3			0x2F //背光切换3
#define		KEY_LED_SET_4			0x30 //背光切换4
#define		KEY_LED_SET_5			0x31 //背光切换5
#define		KEY_LED_SET_6			0x32 //背光切换6

#define	    KEY_LED_DEFINE			0x33 //背光自定义
#define     KEY_DEFINE_SET_1  		0X34 //自定义切换1
#define     KEY_DEFINE_SET_2  		0X35 //自定义切换2
#define     KEY_DEFINE_SET_3  		0X36 //自定义切换3
#define     KEY_DEFINE_SET_4  		0X37 //自定义切换4
#define     KEY_DEFINE_SET_5  		0X38 //自定义切换5
#define     KEY_DEFINE_SET_6  		0X39 //自定义切换6

#define	    KEY_LOGO_SET			0x3A //LOGO灯效设置
#define		KEY_LOGO_COLOUR_SET   	0X3B //LOGO灯七色+幻彩颜色循环切换
#define		KEY_LOGO_LIGHT_UP		0X3C //LOGO灯背光亮度+
#define		KEY_LOGO_LIGHT_DOWN		0X3D //LOGO灯背光亮度-
#define		KEY_LOGO_SPEED_ADD		0X3E //LOGO灯背光速度+
#define		KEY_LOGO_SPEED_DEC		0X3F //LOGO灯背光速度-

#define	    KEY_SIDE_SET			0x40 //SIDE灯效设置
#define		KEY_SIDE_COLOUR_SET   	0X41 //SIDE灯七色+幻彩颜色循环切换
#define		KEY_SIDE_LIGHT_UP		0X42 //SIDE灯背光亮度+
#define		KEY_SIDE_LIGHT_DOWN		0X43 //SIDE灯背光亮度-
#define		KEY_SIDE_SPEED_ADD		0X44 //SIDE灯背光速度+
#define		KEY_SIDE_SPEED_DEC		0X45 //SIDE灯背光速度-
#define		KEY_SIDE_LIGHT_SET		0X46 //SIDE灯背光亮度循环切换

/************ 0x50~0x5F *******************/
#define		KEY_G_CLASS				0X50 //宏集合
#define		KEY_G1					0X51
#define		KEY_G2					0X52
#define		KEY_G3					0X53
#define		KEY_G4					0X54
#define		KEY_G5					0X55
#define		KEY_G6					0X56

/************ 0x60~0x6F *******************/
#define		SWITCH_BT_MODE			0X60 //开关切换到蓝牙模式
#define		SWITCH_2P4G_MODE		0X61 //开关切换到2.4G模式
#define		PAIR_KEY	        	0X62 //配对键
#define		SWITCH_BT_1   		    0X63 //按键切换到蓝牙1
#define		SWITCH_BT_2   		    0X64 //按键切换到蓝牙2 
#define		SWITCH_BT_3   		    0X65 //按键切换到蓝牙3
#define		SWITCH_2D4G				0X66 //按键切换到2.4G模式
#define     SWITCH_USB              0X67 //按键切换到USB模式
#define     BATT_VALUE_SHOW         0X68 //电池电量显示
#define     SWITCH_WIN_MODE         0X69 //切换到WIN模式
#define     SWITCH_MAC_MODE         0X6A //切换到MAC模式

#define     SWITCH_USER_LAYER       0X6C //切换到用户自定义层
#define     SWITCH_OFFICE_LAYER     0X6D //切换到办公配置层
#define     SWITCH_GAME_LAYER       0X6E //切换到游戏配置层
#define     KEY_LED_TEST_MODE       0X6F //全键白光



#endif