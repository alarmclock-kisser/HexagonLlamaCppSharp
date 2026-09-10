typedef void * ggml_backend_reg_t;

extern ggml_backend_reg_t ggml_backend_hexagon_reg(void);

__attribute__((visibility("default"))) ggml_backend_reg_t ggml_backend_init(void)
{
	return ggml_backend_hexagon_reg();
}

__attribute__((visibility("default"))) int ggml_backend_score(void)
{
	return 100;
}
