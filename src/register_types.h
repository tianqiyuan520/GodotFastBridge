#ifndef GODOT_FAST_BRIDGE_REGISTER_TYPES_H
#define GODOT_FAST_BRIDGE_REGISTER_TYPES_H

#include <godot_cpp/core/class_db.hpp>

void initialize_gdextension_types(godot::ModuleInitializationLevel p_level);
void uninitialize_gdextension_types(godot::ModuleInitializationLevel p_level);

#endif // GODOT_FAST_BRIDGE_REGISTER_TYPES_H
